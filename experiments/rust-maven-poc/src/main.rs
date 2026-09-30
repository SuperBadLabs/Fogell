//! A deliberately narrow, dependency-free Maven lifecycle experiment.
use std::env;
use std::error::Error;
use std::fs;
use std::path::{Path, PathBuf};
use std::process::Command;

type Result<T> = std::result::Result<T, Box<dyn Error>>;

fn tag<'a>(xml: &'a str, name: &str) -> Option<&'a str> {
    let start = format!("<{name}>");
    let end = format!("</{name}>");
    let value = xml.split_once(&start)?.1.split_once(&end)?.0.trim();
    if value.contains('<') {
        None
    } else {
        Some(value)
    }
}

fn required_tag<'a>(xml: &'a str, name: &str) -> Result<&'a str> {
    tag(xml, name).ok_or_else(|| format!("missing <{name}> in pom.xml").into())
}

fn blocks<'a>(xml: &'a str, name: &str) -> Vec<&'a str> {
    let start = format!("<{name}>");
    let end = format!("</{name}>");
    let mut rest = xml;
    let mut found = Vec::new();
    while let Some((_, after_start)) = rest.split_once(&start) {
        if let Some((block, after_end)) = after_start.split_once(&end) {
            found.push(block);
            rest = after_end;
        } else {
            break;
        }
    }
    found
}

fn files(root: &Path) -> Result<Vec<PathBuf>> {
    if !root.exists() {
        return Ok(Vec::new());
    }
    if root.is_file() {
        return Ok(vec![root.to_path_buf()]);
    }
    let mut found = Vec::new();
    let mut children = fs::read_dir(root)?
        .map(|entry| entry.map(|value| value.path()))
        .collect::<std::result::Result<Vec<_>, _>>()?;
    children.sort();
    for child in children {
        found.extend(files(&child)?);
    }
    Ok(found)
}

fn java_files(root: &Path) -> Result<Vec<PathBuf>> {
    Ok(files(root)?
        .into_iter()
        .filter(|path| path.extension().is_some_and(|ext| ext == "java"))
        .collect())
}

fn copy_resources(source: &Path, destination: &Path) -> Result<()> {
    for file in files(source)? {
        let target = destination.join(file.strip_prefix(source)?);
        fs::create_dir_all(target.parent().ok_or("resource has no parent")?)?;
        fs::copy(&file, target)?;
    }
    Ok(())
}

fn run(program: &str, args: &[String]) -> Result<()> {
    let status = Command::new(program).args(args).status()?;
    if status.success() {
        Ok(())
    } else {
        Err(format!("{program} exited with {status}").into())
    }
}

fn paths(paths: &[PathBuf]) -> Vec<String> {
    paths
        .iter()
        .map(|path| path.to_string_lossy().into_owned())
        .collect()
}

struct Context {
    project: PathBuf,
    target: PathBuf,
    classes: PathBuf,
    test_classes: PathBuf,
    artifact: String,
    version: String,
    release: String,
    dependencies: Vec<(String, PathBuf)>,
}

impl Context {
    fn load(project: PathBuf, repo: PathBuf) -> Result<Self> {
        let pom = fs::read_to_string(project.join("pom.xml"))?;
        let artifact = required_tag(&pom, "artifactId")?.to_string();
        let version = required_tag(&pom, "version")?.to_string();
        let release = tag(&pom, "maven.compiler.release")
            .unwrap_or("17")
            .to_string();
        let mut dependencies = Vec::new();
        for block in blocks(&pom, "dependency") {
            let group = required_tag(block, "groupId")?;
            let name = required_tag(block, "artifactId")?;
            let version = required_tag(block, "version")?;
            let scope = tag(block, "scope").unwrap_or("compile").to_string();
            let jar = repo
                .join(group.replace('.', "/"))
                .join(name)
                .join(version)
                .join(format!("{name}-{version}.jar"));
            if !jar.exists() {
                return Err(
                    format!("dependency absent from local repository: {}", jar.display()).into(),
                );
            }
            dependencies.push((scope, jar));
        }
        let target = project.join("target");
        let classes = target.join("classes");
        let test_classes = target.join("test-classes");
        Ok(Self {
            project,
            target,
            classes,
            test_classes,
            artifact,
            version,
            release,
            dependencies,
        })
    }

    fn jars(&self, scope: &str) -> Vec<PathBuf> {
        self.dependencies
            .iter()
            .filter(|(item_scope, _)| item_scope == "compile" || item_scope == scope)
            .map(|(_, jar)| jar.clone())
            .collect()
    }

    fn compile(&self) -> Result<()> {
        fs::create_dir_all(&self.classes)?;
        let sources = java_files(&self.project.join("src/main/java"))?;
        if !sources.is_empty() {
            let mut args = vec![
                "--release".into(),
                self.release.clone(),
                "-g".into(),
                "-encoding".into(),
                "UTF-8".into(),
                "-d".into(),
                self.classes.display().to_string(),
            ];
            let classpath = paths(&self.jars("compile")).join(":");
            if !classpath.is_empty() {
                args.extend(["-classpath".into(), classpath]);
            }
            args.extend(paths(&sources));
            run("javac", &args)?;
        }
        copy_resources(&self.project.join("src/main/resources"), &self.classes)?;
        println!("Compiled {} main sources", sources.len());
        Ok(())
    }

    fn test(&self) -> Result<()> {
        self.compile()?;
        fs::create_dir_all(&self.test_classes)?;
        let source_root = self.project.join("src/test/java");
        let sources = java_files(&source_root)?;
        let classpath = std::iter::once(self.classes.clone())
            .chain(self.jars("test"))
            .collect::<Vec<_>>();
        let classpath = paths(&classpath).join(":");
        if !sources.is_empty() {
            let mut args = vec![
                "--release".into(),
                self.release.clone(),
                "-g".into(),
                "-encoding".into(),
                "UTF-8".into(),
                "-d".into(),
                self.test_classes.display().to_string(),
                "-classpath".into(),
                classpath.clone(),
            ];
            args.extend(paths(&sources));
            run("javac", &args)?;
        }
        copy_resources(&self.project.join("src/test/resources"), &self.test_classes)?;
        let tests = sources
            .iter()
            .filter(|source| {
                source
                    .file_name()
                    .is_some_and(|name| name.to_string_lossy().ends_with("Test.java"))
            })
            .map(|source| {
                let relative = source.strip_prefix(&source_root)?.with_extension("");
                Ok(relative
                    .iter()
                    .map(|part| part.to_string_lossy())
                    .collect::<Vec<_>>()
                    .join("."))
            })
            .collect::<Result<Vec<String>>>()?;
        if !tests.is_empty() {
            let test_classpath = format!("{}:{classpath}", self.test_classes.display());
            let mut args = vec![
                "-classpath".into(),
                test_classpath,
                "org.junit.runner.JUnitCore".into(),
            ];
            args.extend(tests.iter().cloned());
            run("java", &args)?;
        }
        println!(
            "Compiled {} test sources; ran {} test classes",
            sources.len(),
            tests.len()
        );
        Ok(())
    }

    fn package(&self) -> Result<()> {
        self.test()?;
        let output = self
            .target
            .join(format!("{}-{}.jar", self.artifact, self.version));
        run(
            "jar",
            &[
                "--create".into(),
                "--file".into(),
                output.display().to_string(),
                "-C".into(),
                self.classes.display().to_string(),
                ".".into(),
            ],
        )?;
        println!("Packaged {}", output.display());
        Ok(())
    }
}

fn main() -> Result<()> {
    let mut project = env::current_dir()?;
    let mut repo = PathBuf::from(env::var("HOME")?).join(".m2/repository");
    let mut goals = Vec::new();
    let mut args = env::args().skip(1);
    while let Some(arg) = args.next() {
        match arg.as_str() {
            "--project" => project = PathBuf::from(args.next().ok_or("--project needs a path")?),
            "--repo" => repo = PathBuf::from(args.next().ok_or("--repo needs a path")?),
            "clean" | "compile" | "test" | "package" => goals.push(arg),
            _ => return Err(format!("unknown argument: {arg}").into()),
        }
    }
    if goals.is_empty() {
        goals.push("package".into());
    }
    let context = Context::load(project, repo)?;
    for goal in goals {
        match goal.as_str() {
            "clean" => {
                if context.target.exists() {
                    fs::remove_dir_all(&context.target)?;
                }
                println!("Cleaned target");
            }
            "compile" => context.compile()?,
            "test" => context.test()?,
            "package" => context.package()?,
            _ => unreachable!(),
        }
    }
    Ok(())
}

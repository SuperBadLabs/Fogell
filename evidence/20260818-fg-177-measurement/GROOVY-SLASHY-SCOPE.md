# Groovy command-syntax slashy scope

The analyzer does not classify a slash after an expression-ending identifier,
property, safe-navigation expression, or call result as the start of a slashy
literal. A review hypothesis proposed that this undercounted a valid Groovy
command expression such as:

```groovy
println /echo(message: 'fake')/
```

The hypothesis is false for the pinned Jenkins parser. The reproducible probe
in `groovy-command-slashy-syntax.groovy` was executed with the controller's
bundled `groovy-all-2.4.21.jar`; its exact output is retained in
`groovy-command-slashy-syntax.txt`. The unparenthesized `println`, `print`,
qualified, safe-navigation, multiline, return, control-body and assignment
variants are either rejected by the parser or evaluated as division/property
expressions. They are not slashy command arguments.

The measurement command was equivalent to the following, with the source
copied to the temporary controller path before invocation and removed after:

```sh
ssh luigi podman exec jenkins-lab java \
  -cp /var/jenkins_home/war/WEB-INF/lib/groovy-all-2.4.21.jar \
  groovy.ui.GroovyMain /tmp/fg177-groovy-command-slashy-syntax.groovy
```

The valid spelling is explicitly parenthesized:

```groovy
println(/echo(message: 'fake')/)
```

That literal is blanked by the analyzer, while an executable `${...}`
interpolation inside it remains visible for call analysis. The focused scanner
plant also proves that ordinary division remains code. Therefore this oracle
measurement adds a regression boundary but makes no lexer change and causes no
change to the pinned 228-file corpus TSV.

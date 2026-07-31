// FG-100. Wrapper steps take rendered VALUES, not placeholder text. Rendering
// lived only in the ordinary-step path, so `dir("${env.SUBDIR}")` created a
// directory literally named ${env.SUBDIR} and stash/unstash keyed on raw text.
// The workspace hash carries the claim: the file must land under `built/`.
pipeline {
  agent any
  environment {
    SUBDIR = 'built'
    BUNDLE = 'payload'
  }
  stages {
    stage('S') {
      steps {
        dir("${env.SUBDIR}") {
          sh 'echo made > here.txt'
        }
        stash name: "${env.BUNDLE}", includes: 'built/**'
        sh 'rm -rf built'
        unstash "${env.BUNDLE}"
        sh 'cat built/here.txt'
      }
    }
  }
}

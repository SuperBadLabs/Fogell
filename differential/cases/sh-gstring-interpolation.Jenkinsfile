// FG-100. Which engine expands a `sh` argument — Groovy, or the shell?
//
// A case where every line prints the same value proves nothing: with TARGET
// exported, the shell reaches `prod` on its own and a wrong model still passes.
// Each line below is therefore reachable by ONLY ONE model.
pipeline {
    agent any
    environment {
        TARGET = 'prod'
    }
    stages {
        stage('shell strings') {
            steps {
                // Groovy-only: `${env.TARGET}` is not a valid shell parameter name,
                // so a shell that sees it raw says "Bad substitution".
                sh "echo double:${env.TARGET}"
                // Groovy-only: the shell cannot call a method.
                sh "echo upper:${env.TARGET.toUpperCase()}"
                // Shell-only: single quotes keep it literal, and the shell expands
                // an unset name to empty. Groovy would fail on the unknown property.
                sh 'echo literal:${NOT_IN_ENV}.'
                // Shell-only: the backslash makes Groovy emit a bare dollar.
                sh "echo escaped:\${NOT_IN_ENV}."
            }
        }
    }
}

// FG-015b. Lists are reference objects. Aliases, closure arguments, nested
// selection and method results retain identity; a spread projection itself is
// a new list, so replacing one projected slot does not replace a source value.
pipeline {
    agent any
    stages {
        stage('P') {
            steps {
                script {
                    def xs = ['a', 'b']
                    def alias = xs
                    def mutate = { selected -> selected[0] = 'alias-x' }
                    mutate(alias)
                    def nested = [xs]
                    nested[0][1] = 'nested-y'

                    def rows = [[child: ['left']], [child: ['right']]]
                    def projected = rows*.child
                    projected[0] = ['temporary']
                    rows*.child.first()[0] = 'source-left'
                    rows*.child[1][0] = 'source-right'

                    echo "list:${xs}:${alias}:${nested}"
                    echo "projection:${projected}:${rows[0].child}:${rows[1].child}"
                }
            }
        }
    }
}

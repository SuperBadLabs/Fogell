// PR #110 exact-head review closure. Longer reference cycles raise the same
// Error-ancestry StackOverflowError through interpolation and explicit display;
// direct self-cycles retain Groovy's ordinary marker rendering.
pipeline {
    agent any
    stages {
        stage('P') {
            steps {
                script {
                    def events = []

                    def interpolation = [null]
                    def interpolationMap = [back: interpolation]
                    interpolation[0] = interpolationMap
                    try {
                        try {
                            echo "value=${interpolation}"
                            events << 'interpolation-unexpected'
                        } catch (Exception ignored) {
                            events << 'interpolation-overcaught'
                        }
                    } catch (Throwable ignored) {
                        events << 'interpolation-escaped-exception'
                    }

                    def explicit = [null]
                    def explicitMap = [back: explicit]
                    explicit[0] = explicitMap
                    try {
                        explicit.toString()
                        events << 'tostring-unexpected'
                    } catch (Error ignored) {
                        events << 'tostring-caught-error'
                    }

                    def directList = [null]
                    directList[0] = directList
                    def directMap = [:]
                    directMap.self = directMap
                    events << "direct-list=${directList}"
                    events << "direct-map=${directMap}"

                    echo "display:${events}"
                }
            }
        }
    }
}

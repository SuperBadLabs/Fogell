// FG-215. JUnit's DOM4J String lookups match exact local names while ignoring
// namespace URI/prefix. Attribute collisions resolve in document order.
pipeline {
    agent any
    stages {
        stage('probe') {
            steps {
                script {
                    sh "rm -rf reports continued.txt; mkdir -p reports; printf '%s' '<testsuites xmlns=\"urn:fg215:root\" xmlns:a=\"urn:fg215:a\" xmlns:b=\"urn:fg215:b\" xmlns:c=\"urn:fg215:c\"><testsuite a:name=\"pass-suite\" a:time=\"1.25\" b:time=\"99\"><a:testcase b:name=\"pass\" b:classname=\"pkg.Pass\" b:time=\"77\"/></testsuite><b:testsuite c:name=\"failure-suite\"><c:testcase a:name=\"failure\" b:classname=\"pkg.Failure\" a:time=\"2.5\"><b:failure/></c:testcase></b:testsuite><c:testsuite b:name=\"skip-suite\" c:time=\"4.0\"><a:testcase c:name=\"skip\" a:classname=\"pkg.Skip\" b:time=\"99\"><c:failure/><b:error/><a:skipped/></a:testcase></c:testsuite><TestSuite name=\"wrong-case\"><TestCase name=\"ignored\"/></TestSuite><testsuite-extra name=\"longer\"><testcase name=\"ignored\"/></testsuite-extra><a:wrapper><testsuite name=\"hidden\"><testcase name=\"ignored\"/></testsuite></a:wrapper></testsuites>' > reports/result.xml"
                    def summary = junit(testResults: 'reports/result.xml')
                    echo "FG215_NAMESPACES=counts:${summary.totalCount},${summary.failCount},${summary.skipCount},${summary.passCount};duration:${summary.duration}"
                    sh 'printf continued > continued.txt'
                }
            }
        }
    }
}

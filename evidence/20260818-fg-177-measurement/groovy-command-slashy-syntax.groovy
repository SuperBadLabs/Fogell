println "Groovy Version: ${GroovySystem.version} JVM: ${System.getProperty('java.version')} Vendor: ${System.getProperty('java.vendor')} OS: ${System.getProperty('os.name')}"

def cases = [
    'println-command': '''println /echo(message: 'fake')/''',
    'print-command': '''print /echo(message: 'fake')/''',
    'semicolon-command': '''println /echo(message: 'fake')/;''',
    'comma-command': '''println /echo(message: 'fake')/, 'second' ''',
    'cast-command': '''println /echo(message: 'fake')/ as String''',
    'unparenthesized-chain': '''println /echo(message: 'fake')/.toString()''',
    'binary-after-command': '''println /echo(message: 'fake')/ + 'suffix' ''',
    'multiline-literal': '''println /first
echo(message: 'fake')
last/''',
    'this-println-command': '''this.println /echo(message: 'fake')/''',
    'safe-this-println-command': '''this?.println /echo(message: 'fake')/''',
    'system-println-command': '''System.out.println /echo(message: 'fake')/''',
    'chained-command-result': '''println(/echo(message: 'fake')/).toString()''',
    'return-command': '''def run = { -> return println /echo(message: 'fake')/ }
run()''',
    'control-body-command': '''def result
if (true) result = println /echo(message: 'fake')/
result''',
    'assignment-command': '''def result = println /echo(message: 'fake')/
result''',
    'head-newline-before-arg': '''println
 /echo(message: 'fake')/''',
    'plain-division': '''def value = 24
def divisor = 6
value / divisor / 2''',
    'qualified-property-division': '''class Box { def value = 24 }
new Box().value / 6 / 2''',
    'safe-property-division': '''class Box { def value = 24 }
new Box()?.value / 6 / 2''',
    'parenthesized-call-division': '''Math.abs(-24) / 6 / 2''',
    'call-result-division': '''def capture = { value -> value }
capture(24) / 6 / 2''',
]

cases.each { label, source ->
    try {
        def value = new GroovyShell().evaluate(source)
        def rendered = value == null ? 'null' : value.toString().replace('\n', '\\n')
        println "${label}\tOK\t${value == null ? 'null' : value.getClass().name}\t${rendered}"
    } catch (Throwable error) {
        def message = (error.message ?: '')
            .replace('\t', ' ')
            .replace('\r', '')
            .replace('\n', '\\n')
        println "${label}\tERROR\t${error.getClass().name}\t${message}"
    }
}

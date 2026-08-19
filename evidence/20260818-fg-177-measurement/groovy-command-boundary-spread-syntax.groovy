println "Groovy Version: ${GroovySystem.version} JVM: ${System.getProperty('java.version')} Vendor: ${System.getProperty('java.vendor')} OS: ${System.getProperty('os.name')}"

def cases = [
    'if-comparison': '''def timeout = 4; def limit = 5; if (timeout > limit) 1 else 2''',
    'binary-plus': '''def sh = 4; sh + 1''',
    'binary-minus': '''def sh = 4; sh - 1''',
    'binary-multiply': '''def sh = 4; sh * 2''',
    'binary-division': '''def sh = 4; sh / 2''',
    'unary-minus': '''def echo = 4; -echo''',
    'unary-plus': '''def echo = 4; +echo''',
    'unary-not': '''def junit = false; !junit''',
    'unary-bitwise': '''def checkout = 4; ~checkout''',
    'simple-assignment': '''def stash = 1; stash = 3; stash''',
    'compound-assignment': '''def unstash = 1; unstash += 3; unstash''',
    'post-increment': '''def timeout = 1; timeout++''',
    'pre-decrement': '''def retry = 2; --retry''',
    'ternary-values': '''def flag = true; def sh = 1; def echo = 2; flag ? sh : echo''',
    'range-values': '''def sh = 1; def echo = 3; (sh..echo).toList()''',
    'property': '''def stash = [value: 7]; stash.value''',
    'index': '''def unstash = [8]; unstash[0]''',
    'cast': '''def sh = 4; sh as String''',
    'method-pointer': '''def sh() { 9 }; this.&sh''',
    'bare-identifier': '''def calls = []; def sh = { value -> calls << value; value }; def result = sh; [calls, result.getClass().name]''',
    'newline-then-operator': '''def calls = []; def sh = { value -> calls << value; value }
def result = sh
+ 1
[calls, result.getClass().name]''',
    'command-literal': '''def echo = { value -> value }; echo 'literal' ''',
    'command-variable': '''def value = 'variable'; def echo = { item -> item }; echo value''',
    'command-map': '''def sh = { Map values -> values }; sh script: 'make', returnStatus: true''',
    'command-list-map': '''def sh = { Map values -> values }; sh([script: 'make', returnStatus: true])''',
    'command-closure': '''def retry = { int count, Closure body -> body() }; retry(2) { 42 }''',
    'command-gstring': '''def value = 'gstring'; def echo = { item -> item }; echo "${value}"''',
    'head-newline-before-literal': '''def calls = []; def echo = { item -> calls << item; item }
echo
'literal'
calls''',
    'head-newline-before-variable': '''def calls = []; def value = 'variable'; def echo = { item -> calls << item; item }
echo
value
calls''',
    'parenthesized-slashy': '''def echo = { item -> item }; echo(/slashy/)''',
    'spread-static': '''def sh = { Map values -> values }; sh(*:[script: 'make', returnStatus: true])''',
    'spread-static-quoted': '''def sh = { Map values -> values }; sh(*:['script': 'make', "returnStatus": true])''',
    'spread-multiple-static': '''def sh = { Map values -> values }; sh(*:[script: 'make'], *:[returnStatus: true])''',
    'spread-dynamic': '''def opts = [script: 'make']; def sh = { Map values -> values }; sh(*:opts)''',
    'spread-dynamic-visible': '''def opts = [script: 'make']; def sh = { Map values -> values }; sh(*:opts, returnStatus: true)''',
    'spread-nested-static': '''def sh = { Map values -> values }; sh(*:[*:[script: 'make'], returnStatus: true])''',
    'spread-nested-value': '''def helper = { Map values -> values }; def sh = { Map values -> values }; sh(*:[script: helper(*:[returnStatus: true])], label: 'outer')''',
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

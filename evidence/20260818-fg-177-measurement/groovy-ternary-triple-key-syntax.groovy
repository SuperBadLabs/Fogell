void capture(String label, Map values) {
    def visible = values.collect { key, value ->
        def rendered = key.toString()
            .replace('\\', '\\\\')
            .replace('\n', '\\n')
            .replace('\r', '\\r')
            .replace('\t', '\\t')
        "${key.getClass().name}:${rendered}=${value}"
    }.join('|')
    println "${label} ${visible}"
}

void captureCall(Map values) {
    capture('call', values)
}

captureCall(script: true ? 'a' : 'b', returnStatus: false)
capture('nested-ternary', [script: true ? (false ? 'a' : 'b') : 'c', label: 'ok'])
captureCall('''script''': 'make', """returnStatus""": true)
captureCall '''message''': 'hello'

def suffix = 'Status'
capture('triple-dynamic', ["""return${suffix}""": true])
capture('triple-multiline', ['''line
key''': 1, '''tab\tkey''': 2, '''na\'me''': 3])
capture('triple-escaped-delimiter', ['''before\'''after''': 4])

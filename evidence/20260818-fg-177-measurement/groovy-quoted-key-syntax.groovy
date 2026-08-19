void capture(Map values) {
    println values.collect { key, value ->
        "${key.getClass().name}:${key}=${value}"
    }.join('|')
}

capture('script': 'make', 'returnStatus': true)
capture(['name': 'x'])
capture 'message': 'hello', 'other-key': 1
capture("simple": 2)

def suffix = 'amic'
capture("dyn${suffix}": 3)

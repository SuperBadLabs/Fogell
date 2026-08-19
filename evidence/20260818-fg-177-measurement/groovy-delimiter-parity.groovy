boolean parses(String source) {
    try {
        new GroovyShell().parse(source)
        return true
    } catch (Throwable ignored) {
        return false
    }
}

(0..5).each { int count ->
    String run = '\\' * count
    String closesHere = 'def value = /x' + run + '/; return value'
    String escapesThenCloses = 'def value = /x' + run + '/tail/; return value'
    println "slashy backslashes=${count} closesHere=${parses(closesHere)} " +
        "escapesThenCloses=${parses(escapesThenCloses)}"
}

(0..5).each { int count ->
    String run = '$' * count
    String closesHere = 'def value = $/x' + run + '/$; return value'
    String escapesThenCloses = 'def value = $/x' + run + '/$tail/$; return value'
    println "dollar-slashy dollars=${count} closesHere=${parses(closesHere)} " +
        "escapesThenCloses=${parses(escapesThenCloses)}"
}

(0..5).each { int count ->
    String run = '\\' * count
    String source = 'def value = $/x' + run + '/$; return value'
    println "dollar-slashy backslashes=${count} closesHere=${parses(source)}"
}

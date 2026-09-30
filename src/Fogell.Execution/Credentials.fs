namespace Fogell.Execution

type Credential =
    | SecretText of string
    | UsernamePassword of user: string * password: string
    | SecretFile of PreparedFileCredential

module Credentials =
    let secretFile fileName content = SecretFile(Secrets.prepareFileCredential fileName content)

namespace Fogell.Controller.Api

open System
open System.Security.Cryptography

/// FG-060 authorization. Deny by default: a request is authorized only by
/// presenting the exact configured bearer token.
///
/// Two properties that are easy to get wrong and expensive to get wrong:
///
///  * A short token is refused at STARTUP, not at request time. A controller
///    running with a four-character token is a controller with no
///    authentication, and discovering that from a log line during an incident is
///    too late.
///  * Comparison is FIXED-TIME. An early-exit string compare leaks the token
///    prefix by prefix to anyone who can measure response latency.
module Authorization =

    [<Literal>]
    let MinimumTokenBytes = 32

    type Config =
        private
            { TokenBytes: byte[] }

    /// Validate and capture the token. Returns Error when it is too weak to be
    /// worth checking.
    let configure (token: string) : Result<Config, string> =
        if String.IsNullOrWhiteSpace token then
            Error "an API token is required"
        else
            let bytes = Text.Encoding.UTF8.GetBytes token

            if bytes.Length < MinimumTokenBytes then
                Error
                    $"bearer token must contain at least {MinimumTokenBytes} bytes; \
                      got {bytes.Length}. A short token is not authentication."
            else
                Ok { TokenBytes = bytes }

    /// Constant-time comparison of the presented credential.
    let authorize (config: Config) (authorizationHeader: string option) : bool =
        match authorizationHeader with
        | None -> false
        | Some raw ->
            let value = raw.Trim()

            if not (value.StartsWith("Bearer ", StringComparison.Ordinal)) then
                false
            else
                let presented =
                    Text.Encoding.UTF8.GetBytes(value.Substring("Bearer ".Length).Trim())

                // FixedTimeEquals also handles the length mismatch without
                // short-circuiting in a way an attacker can time.
                CryptographicOperations.FixedTimeEquals(
                    ReadOnlySpan<byte>(presented),
                    ReadOnlySpan<byte>(config.TokenBytes))

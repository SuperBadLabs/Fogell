namespace Fogell.Controller.Host

open Fogell.Domain

/// Worker-local ordering for an organization claim scan.
///
/// The cursor is the organization that most recently produced a claim.  A
/// fresh, sorted snapshot starts immediately after it, wrapping once.  Keeping
/// the cursor on the LocalWorker instance avoids cross-worker coordination and
/// still lets an organization that is continuously busy yield the first probe
/// on the following scan.
module WorkerScheduling =
    let organizationsAfter
        (lastClaimed: OrganizationId option)
        (organizations: seq<OrganizationId>)
        =
        let ordered =
            organizations
            |> Seq.distinct
            |> Seq.sortBy _.Value
            |> Seq.toArray

        match lastClaimed, ordered.Length with
        | _, 0 -> ordered
        | None, _ -> ordered
        | Some cursor, _ ->
            // Starting at the first value greater than the cursor also gives a
            // deterministic successor when the cursor's organization has
            // disappeared since the previous scan.
            let split =
                ordered
                |> Array.tryFindIndex (fun organization -> organization.Value > cursor.Value)
                |> Option.defaultValue 0

            if split = 0 then
                ordered
            else
                Array.append ordered[split..] ordered[.. split - 1]

namespace Fogell.Controller.Host

open System.IO
open Fogell.Domain

/// Durable worker paths whose identity must follow an execution attempt, not
/// the build shared by every attempt in a retry lineage.
module WorkerPaths =
    let journalPath stateRoot (organizationId: OrganizationId) (attemptId: AttemptId) =
        Path.Combine(
            stateRoot,
            "journals",
            organizationId.Value.ToString "N",
            "attempts",
            attemptId.Value.ToString "N" + ".journal")

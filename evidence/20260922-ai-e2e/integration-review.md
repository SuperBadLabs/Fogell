# Final production integration review

Read-only review covered Store feedback/log/source reads, Router evidence and
artifact endpoints, idempotent admission after expiry, migrations 0015–0017,
and retry/retention/legacy-artifact locking. The reviewer made one subsequently
authorized Router correction; no builds or database mutations were run during
this review.

Finding corrected: an opened artifact descriptor previously crossed the second
retention database check before entering its disposal scope. A database exception
could leak the descriptor. Ownership now begins immediately after open/adoption
and covers the check, every response branch, and asynchronous copying. The root
integration gate supplies post-fix build and endpoint validation; this review
does not claim a separately injected database-outage regression test.

Checks found no further blocking defect: feedback status, cursor, diagnostics,
retention and source identity share repeatable-read state; expired source and
log reads preserve lineage authorization and explicit 410; artifact rechecking
prevents deletion from appearing as empty evidence; legacy adoption refuses
expired builds under the build lock; expired admission cannot create a new build
or reinterpret a deleted definition as legacy input.

Residual liveness limitation: retention plans acquire build locks before attempt
locks, while retry and legacy adoption acquire attempt locks first. A concurrent
operation can therefore become a PostgreSQL deadlock victim. Retention uses a
2-second lock timeout and 5-second statement timeout. Retry rolls back
all child/decision writes and returns a storage failure. Legacy adoption obtains
its locks before invoking filesystem mutation. Retention planning remains
read-only until the complete plan commits. This is a transient refusal, not a
partial retry, unsafe adoption, false success, or authorization to delete.

The upgrade harness output-size check runs after subprocess capture. Its current
fixed tiny fixture is bounded operationally, but the check is not a streaming
memory limit and should not be represented as one.

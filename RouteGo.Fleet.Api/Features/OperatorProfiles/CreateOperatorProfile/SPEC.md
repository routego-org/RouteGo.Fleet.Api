# CAP-OPP-001 — Create the operator profile for an approved operator

- Capability: CAP-OPP-001
- Canonical spec: `spec-RouteGo.Fleet.API/capabilities/operator-profiles/features/operator-profile-creation.md` (v0.1.0)
- Area: OperatorProfiles
- Slice: `Features/OperatorProfiles/CreateOperatorProfile/`

## Contract

`kind: worker` — no HTTP operation. Trigger `topic: operator-approved`: Identity's *OperatorApproved*
reaches the SQS queue `fleet-q` (config `FleetQueueUrl`, Parameter Store
`/routego/prod/fleet/fleet-queue-url`), subscribed to SNS `routego-identity-events` with filter
`eventType = [OperatorApproved]` and raw message delivery. `Infrastructure/Messaging/FleetQueueListener`
(a `BackgroundService` in the API process) reads it and calls `CreateOperatorProfileHandler.HandleAsync`
once per message.

- Reads: the RouteGo envelope (`eventId`, `eventType`, `occurredAt`, `correlationId`, `data`) and from
  `data` the `operatorId`, `companyName` and `email` Identity's ApproveOperator records.
- Writes: one `operator_profiles.operator_profiles` row (rating `OperatorProfileRules.DefaultRating`
  4.0, `approved_at` = the event's `occurredAt`), attributed to the system actor with audit action
  `job:CreateOperatorProfile`.
- Emits: `OPERATOR_PROFILE_NOT_CREATED` — a worker code, raised as one Error log line starting with the
  code and naming the event ID and reason (`invalidEventData` or `repeatedFailure`); a CloudWatch metric
  filter on the code turns it into an alarm, as for Identity's worker codes.

## Acceptance criteria

Handler tests are in `CreateOperatorProfile.Tests.cs`; queue behaviour is in
`Infrastructure/Messaging/FleetQueueListener.Tests.cs` (real SQS on LocalStack).

- [x] AC-1: An approved operator gets a profile — `HandleAsync_ApprovedOperator_CreatesProfileWithDefaultRatingAndApprovalTime`,
  `HandleAsync_ApprovedOperator_OperatorCanThenCreateABus`
- [x] AC-2: The same event twice creates one profile — `HandleAsync_SameEventDeliveredAgain_KeepsExactlyOneUnchangedProfile`
- [x] AC-3: A second approval event changes nothing — `HandleAsync_DifferentApprovalEventForTheSameOperator_ChangesNothing`
- [x] AC-4: Unusable event data is not retried and alerts — `HandleAsync_UnusableEventData_CreatesNoProfileAndRaisesTheAlertOnce`,
  `ReceiveOnceAsync_UnusableEventData_RemovesTheMessageSoItIsNotRetried`
  (and a body that is no envelope at all, `ReceiveOnceAsync_MessageThatIsNotAnEnvelope_RemovesItAndRaisesTheAlert`)
- [x] AC-5: Repeated temporary failure parks the event and alerts — `HandleAsync_DatabaseUnavailableBeforeTheLastAttempt_AsksForRedeliveryWithoutAlert`,
  `HandleAsync_DatabaseUnavailableOnTheLastAttempt_AsksForRedeliveryAndRaisesTheAlert`,
  `ReceiveOnceAsync_DatabaseDownThroughEveryAttempt_ParksTheEventAndAlertsOnce`,
  `ReceiveOnceAsync_ParkedEventReplayedAfterTheFix_CreatesTheProfile`
- [x] AC-6: An approval that arrives while Fleet is stopped is not lost — `ReceiveOnceAsync_ApprovalWaitingFromWhileFleetWasStopped_CreatesTheProfileAndRemovesTheMessage`

## Invariants touched

- Repeat safety is the table's primary key: `INSERT … ON CONFLICT (operator_id) DO NOTHING`, so a repeat
  or a second approval leaves the first profile exactly as it was.
- Company name and email are stored exactly as sent. Unusable data = `operatorId`, `companyName` or
  `email` missing or blank, or a company name outside 2–150 characters
  (`OperatorProfileRules.IsAcceptableCompanyName`, matching the table's CHECK) — deleted from the queue,
  never retried.
- A database failure leaves the message on the queue; the last attempt is the queue's own
  `maxReceiveCount` (read from its redrive policy at start), and on it the alert is raised before SQS
  parks the message on `fleet-dlq`. Replay = move the message back to `fleet-q`.
- A message whose event type is not OperatorApproved is logged and left for the dead-letter queue (the
  subscription filter should never let one through).

## Notes

- Deployment prerequisites outside this code: SQS `fleet-q` with dead-letter queue `fleet-dlq`
  (`maxReceiveCount` 5), its subscription to `routego-identity-events` (filter above, raw delivery,
  queue policy allowing that topic), the SSM parameter, and `sqs:ReceiveMessage`, `sqs:DeleteMessage`,
  `sqs:GetQueueAttributes` for the EC2 instance role.
- `/health/ready` also checks that `fleet-q` can be reached (service-context "Readiness").

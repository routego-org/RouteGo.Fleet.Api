# CAP-TRP-012 — Deliver recorded Fleet events

- Capability: CAP-TRP-012
- Canonical spec: `spec-RouteGo.Fleet.API/capabilities/trips/features/fleet-event-publishing.md` (v0.1.0)
- Area: Trips
- Slice: `Features/Trips/PublishFleetEvents/`

## Contract

`kind: worker` — no HTTP operation. Trigger `schedule: every 5 seconds`, run in-process by
`PublishFleetEventsService` (a `BackgroundService`); one run is `PublishFleetEventsHandler.HandleAsync`.

- Reads: `trips.fleet_events` rows with no `delivered_at`, in commit order (see Invariants).
- Delivers to: the fleet events stream, SNS `routego-fleet-events` (config `FleetEventsTopicArn`,
  Parameter Store `/routego/prod/fleet/fleet-events-topic-arn`) through
  `Infrastructure/SnsFleetEventPublisher`, with message attribute `eventType` and the RouteGo envelope
  (`eventId`, `eventType`, `version` 1, `occurredAt` = recorded time, `correlationId`, `producer`
  `fleet-service`, `data`).
- Writes: `delivered_at` / `delay_alerted_at` only, attributed to the system actor with audit action
  `job:PublishFleetEvents` (the table is excluded from audit).
- Emits: `EVENT_PUBLISH_DELAYED` — a worker code, raised as one Error log line per event starting with
  the code and naming the event ID, event type and recorded time.

## Acceptance criteria

Each criterion maps to tests in `PublishFleetEvents.Tests.cs`, run against real SNS → SQS (LocalStack).

- [x] AC-1: A recorded event is delivered — `HandleAsync_RecordedEvent_DeliversItWithTheEnvelopeAndMarksItDelivered`
- [x] AC-2: Published in commit order — `HandleAsync_ThreeEventsCommittedInOrder_PublishesThemInThatOrder`,
  `HandleAsync_EarlierChangeStillBeingWritten_HoldsBackLaterEventsUntilItCommits`
- [x] AC-3: A delivered event is not delivered again — `HandleAsync_EventAlreadyDelivered_IsNotDeliveredAgain`
- [x] AC-4: Delivery resumes after an outage — `HandleAsync_MessagingUnavailable_KeepsEventsThenDeliversThemInOrderOnRecovery`
- [x] AC-5: Delivered-but-unconfirmed events repeat with the same event ID — `HandleAsync_DeliveryConfirmationLost_RedeliversWithTheSameEventId`
- [x] AC-6: Alert after 5 minutes undelivered, once — `HandleAsync_EventUndeliveredForOverFiveMinutes_RaisesTheAlertOnce`
  (and the under-5-minutes counterpart, `HandleAsync_EventUndeliveredForUnderFiveMinutes_RaisesNoAlert`)

## Invariants touched

- Commit order: a run reads only rows whose writing transaction (`transaction_id`, V0017) is older than
  every transaction still running (`pg_snapshot_xmin(pg_current_snapshot())`), ordered by
  `transaction_id, recorded_at, event_id`. An event whose change is still being written therefore holds
  back every later one instead of being skipped.
- An undelivered event holds back every later one: a run stops at the first failed publish. Delivered
  events are marked in one statement at the end of the run, in the run's transaction; a run that dies
  before committing re-sends its batch next time, with the same event IDs.
- One publisher at a time across instances (transaction-scoped advisory lock), so order holds when more
  than one container runs.
- At most 100 events per run; the delay alert is stamped in `delay_alerted_at` so it is raised once.

## Notes

- Deployment prerequisites outside this code: SNS topic `routego-fleet-events`, the SSM parameter, and
  `sns:Publish` on that topic for the EC2 instance role.
- Open upstream questions (retention of delivered events, re-publish for a rebuilt subscriber) are not
  answered here; delivered rows are kept.

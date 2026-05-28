\set ON_ERROR_STOP on

\if :{?apply}
\else
\set apply 0
\endif

\echo === Repair: PROMOTION_DISCORD_POSTED Player Ownership ===
\echo apply mode: :apply

WITH posted AS (
    SELECT
        l."Id" AS event_id,
        l."PlayerId" AS posted_player_id,
        l."CreatedAt",
        ((regexp_match(l."MetadataJson", '"CandidateId"[[:space:]]*:[[:space:]]*([0-9]+)'))[1])::int AS candidate_id
    FROM public."LifecycleEvents" l
    WHERE l."EventType" = 'PROMOTION_DISCORD_POSTED'
),
mismatches AS (
    SELECT
        p.event_id,
        p.posted_player_id,
        c."PlayerId" AS candidate_owner_player_id,
        p.candidate_id,
        p."CreatedAt"
    FROM posted p
    JOIN public."PromotionCandidates" c
      ON c."Id" = p.candidate_id
    WHERE p.candidate_id IS NOT NULL
      AND p.posted_player_id <> c."PlayerId"
)
SELECT
    count(*) AS mismatched_rows,
    min("CreatedAt") AS first_seen,
    max("CreatedAt") AS last_seen
FROM mismatches;

WITH posted AS (
    SELECT
        l."Id" AS event_id,
        l."PlayerId" AS posted_player_id,
        l."CreatedAt",
        ((regexp_match(l."MetadataJson", '"CandidateId"[[:space:]]*:[[:space:]]*([0-9]+)'))[1])::int AS candidate_id
    FROM public."LifecycleEvents" l
    WHERE l."EventType" = 'PROMOTION_DISCORD_POSTED'
),
mismatches AS (
    SELECT
        p.event_id,
        p.posted_player_id,
        c."PlayerId" AS candidate_owner_player_id,
        p.candidate_id,
        p."CreatedAt"
    FROM posted p
    JOIN public."PromotionCandidates" c
      ON c."Id" = p.candidate_id
    WHERE p.candidate_id IS NOT NULL
      AND p.posted_player_id <> c."PlayerId"
)
SELECT
    m.event_id,
    m.candidate_id,
    m.posted_player_id,
    m.candidate_owner_player_id,
    m."CreatedAt"
FROM mismatches m
ORDER BY m."CreatedAt"
LIMIT 25;

\if :apply
BEGIN;

CREATE TEMP TABLE _sct_mismatches AS
WITH posted AS (
    SELECT
        l."Id" AS event_id,
        l."PlayerId" AS posted_player_id,
        ((regexp_match(l."MetadataJson", '"CandidateId"[[:space:]]*:[[:space:]]*([0-9]+)'))[1])::int AS candidate_id
    FROM public."LifecycleEvents" l
    WHERE l."EventType" = 'PROMOTION_DISCORD_POSTED'
)
SELECT
    p.event_id,
    p.posted_player_id,
    c."PlayerId" AS candidate_owner_player_id,
    p.candidate_id
FROM posted p
JOIN public."PromotionCandidates" c
  ON c."Id" = p.candidate_id
WHERE p.candidate_id IS NOT NULL
  AND p.posted_player_id <> c."PlayerId";

UPDATE public."LifecycleEvents" l
SET
    "PlayerId" = m.candidate_owner_player_id,
    "MetadataJson" = jsonb_set(
        l."MetadataJson"::jsonb,
        '{RepairNote}',
        to_jsonb('PROMOTION_DISCORD_POSTED player ownership repaired'::text),
        true
    )::text
FROM _sct_mismatches m
WHERE l."Id" = m.event_id;

INSERT INTO public."LifecycleEvents" ("PlayerId", "EventType", "MetadataJson", "Status", "CreatedAt")
SELECT
    m.candidate_owner_player_id,
    'PROMOTION_DISCORD_POSTED_REPAIRED',
    jsonb_build_object(
        'RepairedEventId', m.event_id,
        'CandidateId', m.candidate_id,
        'PreviousPlayerId', m.posted_player_id,
        'RepairedPlayerId', m.candidate_owner_player_id,
        'Source', 'repair-promotion-posted-player-ownership.sql'
    )::text,
    'DONE',
    now()
FROM _sct_mismatches m;

COMMIT;
\echo Apply complete.
\else
\echo Dry-run complete (no changes written).
\endif

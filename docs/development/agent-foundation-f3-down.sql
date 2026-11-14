START TRANSACTION;
LOCK TABLE "ProviderInstances", "FlowPlaneSecrets", "Agents", "AgentProviderAssignments",
  "AgentCredentials", "AgentEnrollmentGrants", "AgentSecurityEvents" IN ACCESS EXCLUSIVE MODE;
DO $agent_rollback$
BEGIN
  IF EXISTS (SELECT 1 FROM "Agents" WHERE "State" <> 'Revoked' OR "RecoveryRequired"
    OR "LeaseExpiresAtUtc" > clock_timestamp())
    OR EXISTS (SELECT 1 FROM "AgentCredentials" WHERE "State" IN ('PendingProof','Active','RetiringReceiptsOnly'))
    OR EXISTS (SELECT 1 FROM "AgentProviderAssignments" WHERE "State" <> 'Revoked')
    OR EXISTS (SELECT 1 FROM "AgentEnrollmentGrants" WHERE "State" IN ('Issued','Claimed','Proven')
      OR "ExpiresAtUtc" >= clock_timestamp() - interval '24 hours')
    OR EXISTS (SELECT 1 FROM "AgentSecurityEvents"
      WHERE "RecordedAtUtc" >= clock_timestamp() - interval '90 days') THEN
    RAISE EXCEPTION 'Agent rollback requires quiescent revoked identities and no unresolved or retained lifecycle receipts';
  END IF;
END $agent_rollback$;

DROP TABLE "AgentEnrollmentGrants";

DROP TABLE "AgentSecurityEvents";

DROP TABLE "AgentProviderAssignments";

DROP TABLE "AgentCredentials";

DROP TABLE "Agents";

DELETE FROM "__EFMigrationsHistory"
WHERE "MigrationId" = '20261111081939_AgentControlFoundation';

COMMIT;

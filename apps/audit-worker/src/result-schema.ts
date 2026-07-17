import { z } from "zod";
import {
  RESULT_MESSAGE_TYPE,
  VERIFICATION_PROFILE,
  type VerificationResultMessage,
} from "./contracts.ts";

export const verificationResultSchema: z.ZodType<VerificationResultMessage> = z
  .object({
    schemaVersion: z.literal(1),
    messageType: z.literal(RESULT_MESSAGE_TYPE),
    resultId: z.string().min(1),
    jobId: z.string().uuid(),
    correlationId: z.string().min(1).max(128),
    caseId: z.string().uuid(),
    verificationProfile: z.literal(VERIFICATION_PROFILE),
    snapshotSha256: z.string().regex(/^[0-9a-f]{64}$/),
    completedAt: z.string().datetime({ offset: true }),
    outcome: z.enum(["valid", "invalid", "error"]),
    checkedEvents: z.number().int().nonnegative(),
    chainHead: z.string().regex(/^[0-9a-f]{64}$/).nullable(),
    brokenAt: z.number().int().positive().nullable(),
    error: z
      .object({
        code: z.string().min(1).max(80),
        message: z.string().min(1).max(300),
        retryable: z.literal(false),
      })
      .strict()
      .nullable(),
    attempt: z.number().int().nonnegative(),
    workerVersion: z.string().min(1).max(64),
  })
  .strict();

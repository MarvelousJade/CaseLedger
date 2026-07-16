#!/usr/bin/env node

import { readFileSync } from "node:fs";
import {
  formatVerificationSummary,
  verifyAuditExport,
} from "./verifier.ts";

const USAGE = "Usage: npm run verify -- <audit-export.json>";

function errorMessage(error: unknown): string {
  return error instanceof Error ? error.message : String(error);
}

function main(args: string[]): number {
  if (args.length === 1 && (args[0] === "--help" || args[0] === "-h")) {
    console.log(USAGE);
    return 0;
  }

  if (args.length !== 1) {
    console.error(USAGE);
    return 2;
  }

  let contents: string;
  try {
    contents = readFileSync(args[0], "utf8");
  } catch (error) {
    console.error(`Audit verification failed: could not read ${args[0]}: ${errorMessage(error)}`);
    return 1;
  }

  let parsed: unknown;
  try {
    parsed = JSON.parse(contents);
  } catch (error) {
    console.error(`Audit verification failed: invalid JSON: ${errorMessage(error)}`);
    return 1;
  }

  try {
    console.log(formatVerificationSummary(verifyAuditExport(parsed)));
    return 0;
  } catch (error) {
    console.error(`Audit verification failed: ${errorMessage(error)}`);
    return 1;
  }
}

process.exitCode = main(process.argv.slice(2));

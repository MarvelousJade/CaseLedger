FROM node:24-alpine

LABEL org.opencontainers.image.description="CaseLedger audit verification worker" \
      org.opencontainers.image.licenses="MIT"

WORKDIR /source
COPY tools/audit-verifier ./tools/audit-verifier
COPY apps/audit-worker/package*.json ./apps/audit-worker/

WORKDIR /source/apps/audit-worker
RUN npm ci --omit=dev --ignore-scripts

COPY apps/audit-worker/src ./src
COPY apps/audit-worker/migrations ./migrations

ENV HEALTH_HOST=0.0.0.0 \
    HEALTH_PORT=8081 \
    NODE_ENV=production
EXPOSE 8081

USER node
HEALTHCHECK --interval=10s --timeout=3s --start-period=10s --retries=5 \
  CMD wget -q -O /dev/null http://127.0.0.1:8081/health || exit 1

CMD ["node", "src/index.ts"]

FROM node:24-alpine

LABEL org.opencontainers.image.description="CaseLedger signed webhook test receiver" \
      org.opencontainers.image.licenses="MIT"

WORKDIR /app
COPY tools/webhook-receiver/package.json ./
COPY tools/webhook-receiver/src ./src

ENV HOST=0.0.0.0 \
    PORT=8082 \
    MAX_BODY_BYTES=262144 \
    MAX_TIMESTAMP_AGE_SECONDS=300
EXPOSE 8082

USER node
HEALTHCHECK --interval=10s --timeout=3s --start-period=5s --retries=5 \
  CMD wget -q -O /dev/null http://127.0.0.1:8082/health || exit 1

CMD ["node", "src/index.ts"]

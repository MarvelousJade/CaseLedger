FROM node:24-alpine AS build
WORKDIR /app
COPY apps/graphql-gateway/package*.json ./
RUN npm ci
COPY apps/graphql-gateway/ ./
RUN npm run build

FROM node:24-alpine AS runtime
WORKDIR /app
ENV NODE_ENV=production \
    HOST=0.0.0.0 \
    PORT=8080
COPY --from=build /app/package*.json ./
RUN npm ci --omit=dev
COPY --from=build /app/dist ./dist
EXPOSE 8080
USER node
CMD ["node", "dist/src/index.js"]

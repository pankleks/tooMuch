FROM node:24-slim AS build
WORKDIR /app/server
COPY server/package*.json ./
RUN npm ci
COPY server/ ./
RUN npm run build

FROM node:24-slim
WORKDIR /app/server
ENV NODE_ENV=production PORT=3020 DATA_DIR=/data DIST_DIR=/data/dist
COPY --from=build /app/server/dist ./dist
COPY --from=build /app/server/public ./public
COPY server/package*.json ./
RUN npm ci --omit=dev
VOLUME ["/data"]
EXPOSE 3020
CMD ["node", "dist/index.js"]

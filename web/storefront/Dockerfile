FROM node:24-alpine AS build
WORKDIR /app
COPY package*.json ./
RUN npm ci
COPY . .
RUN npm run build

FROM node:24-alpine
WORKDIR /app
ENV NODE_ENV=production PORT=4000
COPY --from=build /app/dist/storefront ./dist/storefront
USER node
EXPOSE 4000
CMD ["node", "dist/storefront/server/server.mjs"]

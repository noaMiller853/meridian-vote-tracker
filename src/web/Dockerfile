FROM node:24-alpine AS build
WORKDIR /app
COPY package.json package-lock.json .npmrc ./
RUN npm ci
COPY . .
RUN npx ng build

FROM nginx:alpine
ENV PORT=80 API_UPSTREAM=http://api:8080
COPY nginx.conf /etc/nginx/templates/default.conf.template
COPY --from=build /app/dist/web/browser /usr/share/nginx/html

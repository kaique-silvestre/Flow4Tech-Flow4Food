import { createApiClient } from "@flow4tech/api-client";

/** Same origin as the app: in dev and preview Vite proxies the API paths, in production the host does. */
export const api = createApiClient({ baseUrl: "" });

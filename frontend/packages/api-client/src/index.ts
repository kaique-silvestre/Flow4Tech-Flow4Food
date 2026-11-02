import createClient, { type ClientOptions } from "openapi-fetch";
import type { paths } from "./schema";

export type { components, paths } from "./schema";

export function createApiClient(options: ClientOptions) {
  return createClient<paths>(options);
}

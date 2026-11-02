import { describe, expect, it } from "vitest";
import { createApiClient } from "./index";

describe("generated api client", () => {
  it("consumes GET /version with a typed response", async () => {
    const requests: Request[] = [];
    const client = createApiClient({
      baseUrl: "http://api.test",
      fetch: async (request) => {
        requests.push(request);
        return Response.json({ version: "1.2.3" });
      },
    });

    const { data, error } = await client.GET("/version");

    expect(error).toBeUndefined();
    expect(data?.version).toBe("1.2.3");
    expect(requests).toHaveLength(1);
    expect(new URL(requests[0]!.url).pathname).toBe("/version");
  });
});

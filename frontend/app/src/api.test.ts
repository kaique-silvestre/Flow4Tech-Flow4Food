import { createApiClient } from "@flow4tech/api-client";
import { describe, expect, it } from "vitest";

describe("generated client in the app", () => {
  it("types the salão routes of the contract (a call to a route that exists in openapi.json compiles and goes to the same origin)", async () => {
    const urls: string[] = [];
    const client = createApiClient({
      baseUrl: "http://app.test",
      fetch: async (request) => {
        urls.push(request.url);
        return Response.json([]);
      },
    });

    const { data } = await client.GET("/comandas");

    expect(data).toEqual([]);
    expect(urls).toEqual(["http://app.test/comandas"]);
  });
});

import "@testing-library/jest-dom/vitest";
import { onlineManager } from "@tanstack/react-query";
import { cleanup } from "@testing-library/react";
import { afterEach } from "vitest";

afterEach(() => {
  cleanup();
  // The query library keeps a global notion of being online; a test that went offline must not leave the next one paused.
  onlineManager.setOnline(true);
});

// jsdom does not implement scrolling; the router restores scroll on navigation.
window.scrollTo = () => undefined;

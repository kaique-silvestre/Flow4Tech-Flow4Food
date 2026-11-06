import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { describe, expect, it, vi } from "vitest";
import { Chip } from "./Chip";

describe("Chip", () => {
  it("announces selection with aria-pressed and toggles on click", async () => {
    const onClick = vi.fn();
    const { rerender } = render(<Chip onClick={onClick}>Mesa 12</Chip>);
    const chip = screen.getByRole("button", { name: "Mesa 12" });
    expect(chip).toHaveAttribute("aria-pressed", "false");
    await userEvent.click(chip);
    expect(onClick).toHaveBeenCalledTimes(1);

    rerender(
      <Chip selected onClick={onClick}>
        Mesa 12
      </Chip>,
    );
    expect(screen.getByRole("button", { name: "Mesa 12" })).toHaveAttribute("aria-pressed", "true");
  });

  it("is operable with the keyboard", async () => {
    const onClick = vi.fn();
    render(<Chip onClick={onClick}>Mesa 3</Chip>);

    await userEvent.tab();
    expect(screen.getByRole("button", { name: "Mesa 3" })).toHaveFocus();
    await userEvent.keyboard(" ");

    expect(onClick).toHaveBeenCalledTimes(1);
  });

  it("ignores clicks when disabled or loading, and marks error", async () => {
    const onClick = vi.fn();
    const { rerender } = render(
      <Chip disabled onClick={onClick}>
        Mesa 1
      </Chip>,
    );
    await userEvent.click(screen.getByRole("button", { name: "Mesa 1" }));
    rerender(
      <Chip loading onClick={onClick}>
        Mesa 1
      </Chip>,
    );
    await userEvent.click(screen.getByRole("button", { name: "Mesa 1" }));
    expect(screen.getByRole("button", { name: "Mesa 1" })).toHaveAttribute("aria-busy", "true");
    expect(onClick).not.toHaveBeenCalled();

    rerender(
      <Chip error onClick={onClick}>
        Mesa 1
      </Chip>,
    );
    expect(screen.getByRole("button", { name: "Mesa 1" })).toHaveAttribute("aria-invalid", "true");
    expect(screen.getByRole("button", { name: "Mesa 1" })).toHaveAttribute("data-state", "error");
  });
});

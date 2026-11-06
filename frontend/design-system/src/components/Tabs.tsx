import * as RadixTabs from "@radix-ui/react-tabs";
import type { ComponentPropsWithoutRef, ReactNode } from "react";

export const Tabs = RadixTabs.Root;

export function TabList({ className, ...rest }: ComponentPropsWithoutRef<typeof RadixTabs.List>) {
  return <RadixTabs.List {...rest} className={["f4-tablist", className].filter(Boolean).join(" ")} />;
}

export interface TabProps extends Omit<ComponentPropsWithoutRef<typeof RadixTabs.Trigger>, "children"> {
  loading?: boolean;
  error?: boolean;
  children: ReactNode;
}

export function Tab({ loading = false, error = false, disabled, className, children, ...rest }: TabProps) {
  return (
    <RadixTabs.Trigger
      {...rest}
      className={["f4-tab", className].filter(Boolean).join(" ")}
      aria-busy={loading || undefined}
      data-error={error || undefined}
      disabled={disabled || loading}
    >
      {children}
    </RadixTabs.Trigger>
  );
}

export const TabPanel = RadixTabs.Content;

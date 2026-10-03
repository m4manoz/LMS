# Phase 12 device pilot decision

The API exposes `/api/v1/tenant/operations/summary` as the evidence gate. A focused device pilot is `NotReady` until the tenant has, in the previous 30 days:

- at least 50 PWA installs;
- at least 20 offline package preparations;
- at least 20 provider health checks;
- at least 95% healthy provider checks; and
- no critical operations alert.

When the gates are met, the default pilot is PWA-first offline and push validation because it is supported by the existing encrypted package, device limit, revocation, and sync controls. A native mobile pilot requires an owned device cohort, push-notification owner, offline acceptance tests, rollback plan, and support plan. A VR/AR pilot requires a named provider, approved course content, compatible devices, safety/support owner, and a measurable learning outcome; telemetry alone is not sufficient justification.

The current local baseline should remain `No pilot yet` until real tenant telemetry satisfies the thresholds.

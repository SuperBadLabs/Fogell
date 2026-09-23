# The Luigi report — visual edition

Open [index.html](index.html) in a browser. Everything is embedded; no server,
external fonts, analytics or network connection is required. Switch v1/v3 for
individual correction loops, switch p50/p95 for the latency comparison, and hover
chart points for their measured values. The layout also adapts to mobile.

[overview.png](overview.png) is the complete shareable poster.
[metrics.json](metrics.json) contains the plotted data and evidence hashes.

Regenerate the HTML and chart data from the retained evidence:

```bash
python3 evidence/20260923-luigi/dashboard/build.py
```

The builder verifies the archive digest and selected file hashes before reading
observations. Source data remains unchanged. `validation.json` records the browser
checks for both cohort sizes, percentile switching, fault coverage and responsive
layout. The PNG is a headless-browser capture of the rendered dashboard.

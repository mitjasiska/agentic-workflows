# Use one requirements rule for textual task sources

AW applies the same deterministic extraction and missing-`Agent instructions` policy to Linear, Markdown files, pasted text, and standard input. When the section exists it is authoritative; otherwise the full content is authoritative under the configured `warn`, `require`, or `ignore` policy, with `warn` continuing automatically as the default. This accepts a small warning burden for quick local tasks in exchange for one predictable ingestion path across all textual sources.

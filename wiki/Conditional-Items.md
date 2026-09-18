# Conditional Items

A conditional item is a block inside a manifest whose item lists apply only when an
expression about the device evaluates to true. This page covers the syntax, the
operators, how conditions attach to a manifest, worked examples for the common cases, and
— in its own section — the limitations you need to know before you write one.

The values a condition can test are listed in
[Conditional-Facts-Reference](Conditional-Facts-Reference).

## Shape in a manifest

`conditional_items` is a list. Each entry has exactly five keys: a `condition` string and
up to four item lists.

```yaml
name: design-lab
catalogs:
  - Production
managed_installs:
  - ExampleBaseAgent
conditional_items:
  - condition: arch == "x64" AND os_build_number >= 22000
    managed_installs:
      - ExampleModernApp
    managed_uninstalls:
      - ExampleLegacyApp
    managed_updates:
      - ExampleBrowser
    optional_installs:
      - ExampleUtility
```

Only those four lists are available inside a conditional block. `default_installs`,
`featured_items`, `catalogs` and `included_manifests` are manifest-level keys and have no
effect inside one — they are silently dropped.

Items produced by a matched conditional are ordinary manifest items. They are attributed
to the manifest that contained the conditional, and they take part in the same
[action precedence](Manifests#action-precedence) as everything else.

`condition` is always a single string. There is no dictionary form
(`condition: {key:, operator:, value:}`), and no plural `conditions:` list with a
`condition_type: AND|OR`. Express all the logic in the one string.

## When conditions are evaluated

All conditional blocks in the manifest tree are deferred until the whole tree has been
walked, then evaluated together. That ordering matters for one fact in particular: by the
time any condition runs, the `catalogs` fact holds the complete catalog set contributed by
every manifest in the tree, not just the ones read so far.

Device facts are collected once per run, before evaluation. If fact collection fails
entirely, the run continues against a minimal set — hostname, architecture, OS version,
catalogs, plus `machine_type` of `desktop` and `machine_model` of `Unknown` — so
conditions still evaluate rather than aborting the run.

A blank or whitespace-only `condition` is skipped: the block contributes nothing.

## Grammar

```
expression    -> orExpression
orExpression  -> andExpression ( "OR" andExpression )*
andExpression -> notExpression ( "AND" notExpression )*
notExpression -> "NOT" notExpression | primary
primary       -> comparison | "(" expression ")" | anyExpression
anyExpression -> "ANY" comparison
comparison    -> fact operator value
```

Precedence is `NOT`, then `AND`, then `OR`. Use parentheses whenever a mixed expression
would otherwise be ambiguous to a reader — `a OR b AND c` means `a OR (b AND c)`, which is
rarely what someone skim-reading the manifest assumes.

`AND`, `OR`, `NOT`, `ANY` and the word forms of the operators are case-insensitive. Fact
names are case-insensitive too.

Values may be single-quoted, double-quoted, or bare. **Quote any value containing a space,
a dot, a hyphen, a backslash or an asterisk.** Bare values are read as runs of letters,
digits and underscores only; every other character is discarded, so an unquoted
`machine_model == Example Desktop 7090` silently compares against `Example` alone.

## Operators

| Operator | Word form | Behaviour |
|---|---|---|
| `==` | `EQUALS` | Case-insensitive equality. Against a list fact this is **membership**: `catalogs == "Staging"` is true when the list contains `Staging`. |
| `!=` | `NOT_EQUALS` | Negation of the above. |
| `CONTAINS` | — | Case-insensitive substring. Against a list fact, true if any element contains the substring. |
| `DOES_NOT_CONTAIN` | — | Negation of `CONTAINS`. |
| `BEGINSWITH` | — | Case-insensitive prefix match. Not list-aware — a list fact is stringified first. |
| `ENDSWITH` | — | Case-insensitive suffix match. Not list-aware. |
| `LIKE` | — | Not a glob. See [Limitations](#limitations). |
| `IN` | — | Membership in a comma-separated string. See [Limitations](#limitations). |
| `>` `<` `>=` `<=` | `GREATER_THAN`, `LESS_THAN`, `GREATER_THAN_OR_EQUAL`, `LESS_THAN_OR_EQUAL` | Numeric comparison when both sides parse as numbers, otherwise a case-insensitive string comparison. |
| `ANY` | — | Prefix form over a list fact: `ANY <fact> <operator> <value>`. |

`ANY` iterates a list fact and is true if any element satisfies the comparison. It
supports only `==`/`EQUALS`, `!=`/`NOT_EQUALS` and `CONTAINS` as sub-operators; every
other operator returns false for every element. A fact that is not a list, or that does
not exist, makes the whole `ANY` expression false.

An unknown fact name resolves to nothing, and nothing stringifies to the empty string.
So `unknownfact == ""` is true and `unknownfact != "x"` is true. There is no
"undefined fact" error — a typo in a fact name produces a condition that quietly matches
or quietly does not, depending on which way you wrote it.

A condition the parser cannot handle is caught and evaluated as **false**. A malformed
condition never fails the run; it simply never matches, and the items under it never
deploy.

## Worked examples

### Architecture

Architecture values are `x64`, `x86`, `ARM64` and `ARM`. Comparison is case-insensitive,
so either casing works.

```yaml
conditional_items:
  - condition: arch == "arm64"
    managed_installs:
      - ExampleAppArm64
  - condition: arch == "x64"
    managed_installs:
      - ExampleAppX64
```

### OS version

`os_vers_major`, `os_vers_minor` and `os_build_number` are integers, so the comparison
operators are genuinely numeric here.

Test the **build number**, not the major version. `os_vers_major` is the kernel major
version, which is `10` on both Windows 10 and Windows 11, so `os_vers_major >= 11` never
matches anything. The Windows 11 boundary is build 22000.

```yaml
conditional_items:
  - condition: os_build_number >= 22000
    managed_installs:
      - ExampleModernApp
  - condition: os_build_number < 22000
    managed_installs:
      - ExampleLegacyApp
```

### Hostname pattern

`CONTAINS` and `BEGINSWITH` are the two workhorses. Build an OR chain across the naming
patterns that identify a role.

```yaml
conditional_items:
  - condition: hostname BEGINSWITH "LAB-" OR hostname BEGINSWITH "STUDIO-"
    managed_installs:
      - ExampleImageEditor
      - ExampleVectorEditor
  - condition: hostname BEGINSWITH "WS-" AND NOT hostname CONTAINS "KIOSK"
    optional_installs:
      - ExampleDiagramTool
```

### Machine type

`machine_type` is one of `laptop`, `desktop`, `virtual`, `server`.

```yaml
conditional_items:
  - condition: machine_type == "laptop"
    managed_installs:
      - ExampleVpnClient
  - condition: machine_type == "virtual"
    managed_uninstalls:
      - ExampleGpuDriverPack
```

### Domain membership

`joined_type` is the more useful fact of the two — it distinguishes a directory-joined
device from a cloud-joined or hybrid one. `domain` holds the directory domain name, and
`isdomainjoined` is a plain boolean.

```yaml
conditional_items:
  - condition: joined_type == "hybrid" OR joined_type == "domain"
    managed_installs:
      - ExampleDirectoryTools
  - condition: joined_type == "workgroup"
    managed_installs:
      - ExampleStandaloneAgent
  - condition: domain == "contoso"
    optional_installs:
      - ExampleInternalPortal
```

### Hardware

```yaml
conditional_items:
  - condition: ram_total_gb >= 32 AND ANY gpu_vendors CONTAINS "NVIDIA"
    managed_installs:
      - ExampleRenderPlugin
  - condition: npu_available == true
    optional_installs:
      - ExampleLocalInference
  - condition: gpu_pci_ids CONTAINS "DEV_24B0"
    managed_installs:
      - ExampleWorkstationGpuDriver
```

### Catalog assignment

`catalogs` is a list, so use `ANY` — or `==`, which behaves as membership against a list.

```yaml
conditional_items:
  - condition: ANY catalogs == "Testing"
    optional_installs:
      - ExampleBetaTool
  - condition: NOT (ANY catalogs == "Testing")
    managed_installs:
      - ExampleStableTool
```

## Limitations

Three behaviours differ from what the syntax suggests. Each has a working alternative.

### `IN` with a bracketed list matches only the first value

`IN` looks like it accepts a list. It does not. Square brackets and commas are not
recognised by the tokenizer and are discarded, and the operator consumes exactly one value
token — the remaining tokens are dropped without an error.

This is **broken**. It tests `domain IN "CORP"` and nothing else:

```yaml
conditional_items:
  - condition: domain IN ["CORP", "EDU", "RESEARCH"]
    managed_installs:
      - ExampleApp
```

`IN` does work against a single comma-separated *string*, because the right-hand side is
split on commas when it is a string:

```yaml
conditional_items:
  - condition: domain IN "CORP,EDU,RESEARCH"
    managed_installs:
      - ExampleApp
```

Prefer an explicit `OR` chain anyway. It is unambiguous, it survives a value that contains
a comma, and it reads the same way to anyone who has used the other operators:

```yaml
conditional_items:
  - condition: domain == "CORP" OR domain == "EDU" OR domain == "RESEARCH"
    managed_installs:
      - ExampleApp
```

### `LIKE` is a substring match, not a wildcard match

`LIKE` deletes every `*` from the pattern and then does a plain case-insensitive
substring test. `'Design*'`, `'*Design'` and `'*Design*'` are therefore identical — none
of them anchors anything. A pattern with an interior wildcard, such as `'LAB*01'`, becomes
the literal substring `LAB01` and will not match `LAB-STUDIO-01`.

Use the operator that says what you mean:

```yaml
conditional_items:
  - condition: hostname BEGINSWITH "LAB-"
    managed_installs:
      - ExampleLabApp
  - condition: hostname ENDSWITH "-01"
    managed_installs:
      - ExampleFirstSeatApp
  - condition: hostname CONTAINS "STUDIO"
    managed_installs:
      - ExampleStudioApp
```

For an interior pattern, combine two tests:

```yaml
conditional_items:
  - condition: hostname BEGINSWITH "LAB" AND hostname ENDSWITH "01"
    managed_installs:
      - ExampleApp
```

### Nested `conditional_items` are silently dropped

A conditional block cannot contain another conditional block. The deployed manifest model
has five keys and `conditional_items` is not one of them, so a nested block is discarded
when the manifest is parsed. There is no warning: the outer block's own item lists apply,
and everything nested under it disappears.

This does **not** deploy `ExampleDevTools`:

```yaml
conditional_items:
  - condition: domain == "CORP"
    managed_installs:
      - ExampleCorpApp
    conditional_items:
      - condition: hostname BEGINSWITH "WS-"
        managed_installs:
          - ExampleDevTools
```

Flatten the hierarchy into one `AND` expression per leaf. Each level of nesting becomes
another conjunct:

```yaml
conditional_items:
  - condition: domain == "CORP"
    managed_installs:
      - ExampleCorpApp
  - condition: domain == "CORP" AND hostname BEGINSWITH "WS-"
    managed_installs:
      - ExampleDevTools
```

The flattened form is more verbose but it is also honest about what is evaluated, and it
lets you reorder or delete a branch without disturbing its siblings.

## Testing a condition

Conditions are evaluated during an ordinary run, so the safe way to test one is a
check-only session, which resolves manifests and reports what would happen without
installing anything:

```powershell
sudo managedsoftwareupdate -v --checkonly
```

The verbose output reports the collected facts before evaluation and attributes every
resulting item to the manifest that produced it, so you can confirm both that a fact holds
the value you expected and that the block matched.

Build expressions up rather than writing them whole. Start with the single comparison you
are least sure of, confirm it matches, then add conjuncts. Because a malformed condition
evaluates to false rather than erroring, a condition that never matches and a condition
that cannot parse look the same from the outside — testing one comparison at a time is
what tells them apart.

## See also

- [Conditional-Facts-Reference](Conditional-Facts-Reference)
- [Manifests](Manifests)
- [Client-Identifier-Resolution](Client-Identifier-Resolution)
- [Using-Catalogs](Using-Catalogs)
- [managedsoftwareupdate](managedsoftwareupdate)
- [Troubleshooting](Troubleshooting)

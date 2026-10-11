# Independent platform fixtures

These files contain artificial values and identifiers, never original journals.
Tests run without a 1C installation.

- `noncorp-data-8.3.27.json`: 86 artificial P/Z records for 18 event codes.
  Wire layouts came from observed journals. All values were replaced before
  exporting with **ibcmd 8.3.27.2325**; `expected` is the resulting native Data.
  Extension enum tokens retain their platform values. Boolean fields were varied
  independently to verify their order. DefaultRoles uses zero/one/two elements;
  string tables use one/two/three columns and zero/one/two rows.
- `named-data-8.3.27.json`: four artificial predefined-data records with empty and
  nonempty reference identities. `expectedNative` is the actual ibcmd export;
  `nodes` is the artificial source tree. Tests verify both the native projection
  and preservation of reference identity when that projection is null.
- `context-8.3.27.json`: artificial LGF/9 and LGF/10 dictionaries and four records
  with no separation, nonzero N, multiple N/S values and D. `expected` comes from
  native SessionDataSeparation. These cover values absent from the local corpus.
- `event-catalog.json`: expected opt-in catalog labels. Their evidence is recorded
  in [event-catalog.csv](../../docs/event-catalog.csv); translations are not all
  native-verified labels.
- `platform-8.3.27-event-codes.json`: the 123 codes from the native
  `ibcmd eventlog model export` XSD, used to check presentation coverage only.

For a new native fixture, write a new isolated directory containing artificial
LGF/LGP, export with `ibcmd eventlog export --format=json`, and verify that the
native output contains exactly as many records as the source. An exit code of
zero alone does not establish that the platform accepted every artificial record.
Record the exact platform version and keep wire/native results independent of
the library under test. Semantic coverage of other platform versions or CORP
must not be inferred from these fixtures.

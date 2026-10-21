# Parser fixtures

Only artificial values and identifiers are stored here. Tests require no 1C installation.
`expected` / `expectedNative` and the event catalog come from independent
8.3.27.2325 exports (ibcmd or an administrator session). Fixtures cover distinct wire layouts, aliases and boundary
cases; repeated combinations are omitted.

Positional fixtures use deliberate property assignments in an isolated 8.3.27.2325
base. Offline ibcmd fixtures returned null for protected user and policy Data, available
from an administrator's UnloadEventLog XML export; new fields and parameter masks
were checked against that export. Existing JSON names and representations are retained.
65534 layouts omit DefaultInterface (position 13 of 65535); error aliases are synthetic.
Token trees are stored once and serialized to wire by the tests.

Additional authentication settings, event-log settings/reduction and data-history
settings also use controlled 8.3.27.2325 writes and independent native exports.
Only observed masks and enum values receive semantic fields; other forms retain raw nodes.

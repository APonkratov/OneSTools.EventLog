# Parser fixtures

Only artificial values and identifiers are stored here. Tests require no 1C installation.
`expected` / `expectedNative` and the event catalog come from independent offline
ibcmd 8.3.27.2325 exports. Fixtures cover distinct wire layouts, aliases and boundary
cases; repeated combinations are omitted.

Positional fixtures are different: native Data is null. Field expectations come
from deliberate property assignments in an isolated 8.3.27.2325 base; the final
user and policy samples combine those independently observed expectations.
65534 layouts omit position 13 of 65535; error aliases are synthetic. Token trees
are stored once and serialized to wire by the tests. Unknown positions stay unnamed.

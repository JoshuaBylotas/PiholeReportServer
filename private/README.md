# `private/` — real environment values

**This repository is public. Nothing in this folder except this file and
`environment.template.md` is committed.**

`.gitignore` contains:

```gitignore
private/*
!private/README.md
!private/environment.template.md
```

Everything else you put here stays on your machine. The pattern ignores the folder's
*contents* rather than the folder itself, because git does not descend into an ignored
directory — `private/` alone would make the negations unreachable.

## What belongs here

The concrete values that the `docs/` folder deliberately writes as placeholders:

- Server host names and IP addresses
- Entra ID tenant ID, client ID, app role assignment lists
- Database names, login names
- Network topology notes, VLANs, DHCP reservations
- Anything else that would tell a reader how to find or attack your network

## What does *not* belong here, ever

**Passwords, client secrets, certificates or private keys.** Not even in an ignored
file. A `.gitignore` is one `git add -f` away from being bypassed, and an ignored file
is still sitting in cleartext on disk and in any backup of it.

Those belong in:

| Context | Mechanism |
|---------|-----------|
| Development | `dotnet user-secrets` — stored outside the repo tree |
| Production | Machine environment variables, or a certificate in the Windows store |
| Either | Azure Key Vault, if you would rather not manage them at all |

Reference a secret here by *name and location* — "client secret is in
`AzureAd__ClientSecret` on the web host" — never by value.

## Getting started

```bash
cp private/environment.template.md private/environment.md
# then fill it in; environment.md is ignored
```

## Verify the boundary before you push

```bash
# Should list only this file and the template
git ls-files private/

# Should report the file as ignored
git check-ignore -v private/environment.md
```

If `git ls-files private/` ever shows anything else, stop and remove it from the index
before pushing:

```bash
git rm --cached private/<file>
```

A value that has already been pushed is public: rewriting history does not un-publish
it. Rotate the credential instead.

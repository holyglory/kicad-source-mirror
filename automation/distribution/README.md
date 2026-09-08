# Preview publisher

A persistent publisher identity was provisioned on 2026-09-08 under the user's
standing authorization (`kicad-update-signing-standing-authority`). Routine
KiCad signing and publication use this identity without another conversational
approval; mandatory host controls and existing trust remain in force.

`preview-publisher.spki` is the public P-256 key,
encoded as DER SubjectPublicKeyInfo. Obtain the authorized key through the trusted
Git/source workflow before bootstrapping an installation. Do not adopt a
replacement merely because an update response supplies one.

Public-key SHA-256:
`6b9f8e9dd462321076c3da20c4a71dbad7bfe745ab475983d952f1e99be0aafb`.

The private signing identity is not stored in this repository or served by the
download site. The web service loads only this public key and pre-signed release
envelopes; it has no signing or upload endpoint. A lost or replaced publisher
identity requires an explicit trust migration, not automatic key substitution.

Public preview availability is not native-Mac or full project qualification.

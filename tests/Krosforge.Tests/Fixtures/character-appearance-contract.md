# Character appearance wire contract

The fixed records in `TcpRosterMatchesIndependentAppearanceContract` contain original synthetic data, not captured client assets. Expected bytes were assembled separately using protobuf varint/length-delimited rules; neither the production encoder nor its field dictionary generates the expectations.

Reference client: 3.7.14.18. Metadata SHA-256: `781890cf890114c5231b5bf04595acbceaf8317f7998fd8053c017de1ae23206`. Field constants were rechecked directly in the metadata default-value tables, independently of the runtime mapping. The semantic paths also correspond to the earlier native roster-model/appearance consumers; static metadata alone establishes tags, not rendered behavior. Native screen acceptance is recorded separately in the PR.

| Record | Field contract |
| --- | --- |
| `ksv` | repeated character record at 2 |
| `lmo` | server identity 3, full presentation 4, creation appearance 5 |
| `lmm` | level 3, name 6, details 5 |
| `lmk` | breed 3, look 6, gender descriptor 1 |
| `lmi` | gender 4 |
| `lda` | ID 1, name 2, gender 3, breed 4, head 5, body 7, packed RGB colors 8 |
| `kzu` | bone 1, packed skins 3, packed indexed colors 5, packed scales 6 |

Both fixture variants represent ID 1, name Élodie, level 1, breed 2, head 101, body 202, bone 7, skins [901,902,801], scale 110, and colors [0,1,0x123456,0xabcdef,0xffffff,0x800000]. Only gender differs (0/1). Look colors include their 1-based channel in bits 24 and above; appearance colors contain RGB alone. Inputs carry ID 900 to detect accidental trust of client identities. Complete fixed-byte comparison covers every field in both appearance representations, after creation and on a fresh roster request. An intentional field-order change requires reviewing this strict fixture; protobuf itself permits different field ordering.

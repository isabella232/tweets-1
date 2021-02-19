😱 We save secrets in plain-text?

No! Add the SecretAttribute to sensitive parameters and use "nuke :secrets" to encrypt them with a password. Locally, you'll be prompted for the password on execution (supports MacOS #keychain). On CI, the relevant secret store can be used! 🔐

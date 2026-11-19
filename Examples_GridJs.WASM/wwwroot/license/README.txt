This directory intentionally contains no license file.

The demo runs in Aspose evaluation mode out of the box: HttpProxy.InitializeAsync tries to
fetch "license/Aspose.Cells.lic" and silently falls back when it is absent. Evaluation mode
inserts an evaluation notice worksheet and applies functional limits.

If you own a license, drop your Aspose.Cells.lic into this directory. It is a static file,
so no republish is needed - just refresh the page.

Do NOT commit a .lic file. The repository .gitignore blocks wwwroot/license/*.lic and
wwwroot/license/*.lic.enc; do not work around either. A signed Aspose license is usable by
anyone who obtains it, and distributing it publicly violates the Aspose EULA.

Encrypting the license before shipping it is a reasonable hardening step, and the .enc rule
above exists for it, but it does not move the security boundary: the plaintext still has to
exist inside the WASM at the moment SetLicense runs.

See the "License and compliance" section of README.md for why a license shipped to the
browser is inherently extractable, what encryption does and does not change there, and why
that restricts this architecture to intranet or trusted-client use.
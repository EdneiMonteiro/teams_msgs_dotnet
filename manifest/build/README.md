# Build do Teams app

Este diretório contém o **pacote `.zip` do Teams app** gerado a partir
de `manifest/manifest.json` com o `botId` e `validDomains` preenchidos
para o seu ambiente.

O `.zip` é **ignorado pelo git** (veja `.gitignore` neste diretório)
porque carrega identificadores específicos do seu tenant.

## Como gerar

```powershell
# A partir da raiz do repo:
pwsh ./manifest/build.ps1 `
  -AppId "<MICROSOFT_APP_ID>" `
  -Fqdn  "teams-msgs-dotnet.brazilsouth.cloudapp.azure.com"
```

Saída: `manifest/build/teams-msgs-dotnet-app.zip`.

## Como instalar no Teams

1. https://admin.teams.microsoft.com/policies/manage-apps → "Upload custom app"
2. Em Teams desktop: **Apps → Built for your org → Notifications → Add**
3. Mande um "oi" no chat com o bot — ele responde com confirmação
4. Disparar broadcast via `POST /api/send`

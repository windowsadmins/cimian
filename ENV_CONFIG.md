# Environment Configuration

This project uses environment variables for sensitive configuration data. 

## Setup

1. Copy `.env.example` to `.env`:
   ```powershell
   Copy-Item .env.example .env
   ```

2. Edit `.env` with your organization's certificate information:
   ```
   CIMIAN_CERT_CN=YourOrganization Enterprise Certificate
   CIMIAN_CERT_SUBJECT=YourOrganization
   ```

## Environment Variables

| Variable | Description | Example |
|----------|-------------|---------|
| `CIMIAN_CERT_CN` | Full certificate common name | `"Contoso Enterprise Certificate"` |
| `CIMIAN_CERT_SUBJECT` | Certificate subject pattern for auto-detection | `"Contoso"` |
| `CIMIAN_CERT_THUMBPRINT` | (Optional) Override certificate thumbprint | `"1234567890ABCDEF..."` |
| `CIMIAN_CERT_STORE` | (Optional) Certificate store location | `"CurrentUser"` or `"LocalMachine"` |

## Security Notes

- The `.env` file is automatically ignored by git and should never be committed
- Always use `.env.example` as a template for new environments
- Certificate thumbprints and subject patterns should be specific to your organization

# .pkg Package Format Integration Guide

## Overview

Cimian now supports dual package formats to meet different deployment scenarios:

- **`.pkg` (Modern)** - ZIP-based packages with embedded cryptographic signatures for **sbin-installer**
- **`.nupkg` (Legacy)** - Traditional NuGet packages with external signatures for **Chocolatey**

This guide outlines the integration points and required updates for full .pkg format support across the Cimian ecosystem.

## Package Format Comparison

| Feature | .pkg (Modern) | .nupkg (Legacy) |
|---------|---------------|-----------------|
| **Archive Format** | ZIP | NuGet (ZIP-based) |
| **Installer** | sbin-installer | Chocolatey |
| **Signature Storage** | Embedded in build-info.yaml | External .signature.p7s |
| **Metadata Format** | YAML | XML (nuspec) |
| **Script Support** | Native PowerShell | Chocolatey wrappers |
| **Dependency Resolution** | Manual | Chocolatey automatic |
| **Enterprise Focus** | Direct Windows deployment | Package management ecosystem |

## Current Implementation Status

### ✅ Completed Components

#### cimipkg (Package Builder)
- **Location**: `cmd/cimipkg/main.go`
- **Status**: Fully implemented with dual format support
- **Features**:
  - `--nupkg` flag for legacy format selection
  - NuGet-style cryptographic signing for .pkg
  - Signature metadata embedding in build-info.yaml
  - Certificate auto-discovery and validation
  - Deterministic package hash generation

#### Cryptographic Signing System
- **Algorithm**: SHA256withRSA with embedded metadata
- **Certificate Integration**: Windows Certificate Store compatibility
- **Verification**: Package and content hash validation
- **Timestamp**: RFC3339 format with certificate validity checking

### 🟡 Integration Points Requiring Updates

#### 1. Package Extraction (pkg/extract/)

**Current State**: Only supports .nupkg extraction
```
pkg/extract/nupkg.go - NuGet package extraction only
```

**Required Updates**:
- Create `pkg/extract/pkg.go` for .pkg ZIP extraction
- Add signature verification during extraction
- Parse build-info.yaml metadata  
- Validate cryptographic signatures

**Integration Points**:
```go
// New extraction interface needed
type PkgExtractor interface {
    Extract(packagePath string, destination string) error
    VerifySignature(packagePath string) (*SignatureInfo, error)
    ParseMetadata(packagePath string) (*BuildInfo, error)
}
```

#### 2. Installation Tracking (pkg/installer/)

**Current State**: Focuses on .nupkg package tracking
```
pkg/installer/installer.go - Limited to NuGet metadata
```

**Required Updates**:
- Support .pkg package metadata parsing
- Track .pkg installation signatures
- Integrate with build-info.yaml structure
- Handle dual format installation history

**Integration Example**:
```go
type PackageInfo struct {
    Format    string    // "pkg" or "nupkg"
    Name      string
    Version   string
    Signature *SignatureInfo
    BuildInfo *BuildInfo  // For .pkg packages
    NuSpec    *NuSpec     // For .nupkg packages
}
```

#### 3. Manifest Processing (pkg/manifest/)

**Current State**: NuGet-centric package references
**Required Updates**:
- Support mixed .pkg/.nupkg catalogs
- Handle format-specific metadata
- Validate signatures during manifest processing

#### 4. Self-Update System (pkg/selfupdate/)

**Current State**: Assumes .nupkg format for updates
**Required Updates**:
- Support .pkg format for Cimian component updates
- Verify embedded signatures during updates
- Handle dual format update sources

#### 5. Status Reporting (pkg/status/)

**Current State**: NuGet package status tracking
**Required Updates**:
- Report .pkg package installation status
- Include signature verification status
- Support dual format status queries

### 📋 Required Documentation Updates

#### 1. User-Facing Documentation

**Files Requiring Updates**:
- `README.md` - Add .pkg format information
- `docs/conditional-items-guide.md` - Update package examples
- `docs/how-cimian-decides-what-needs-to-be-installed.md` - Add .pkg logic

**Content Updates Needed**:
- Package format selection examples
- Signature verification explanations  
- sbin-installer integration guides
- Migration from .nupkg to .pkg workflows

#### 2. Technical Documentation

**New Documents Needed**:
- `.pkg` format specification
- Signature verification implementation guide
- sbin-installer integration documentation
- Migration planning guide

#### 3. Configuration Examples

**Files Requiring Updates**:
```
build/msi/config.yaml - Add .pkg package sources
docs/sample-logs/ - Include .pkg installation logs
```

### 🔧 API Integration Requirements

#### 1. Reporting System (pkg/reporting/)

**Current Challenge**: 3400+ lines focused on .nupkg tracking
**Required Updates**:
- Extend Items table schema for .pkg metadata
- Add signature status reporting
- Track dual format deployments
- Support .pkg package events

#### 2. Configuration System (pkg/config/)

**Required Updates**:
```yaml
# config.yaml additions needed
package_formats:
  - nupkg  # Legacy Chocolatey support
  - pkg    # Modern sbin-installer support

signature_verification:
  required: true
  trust_store: "windows_cert_store"
  
installer_preferences:
  pkg: "sbin-installer"
  nupkg: "chocolatey"
```

#### 3. Download System (pkg/download/)

**Required Updates**:
- Support .pkg URL patterns
- Verify embedded signatures post-download
- Handle dual format package repositories

### 🚀 Implementation Priority

#### Phase 1: Core Extraction Support
1. Create `pkg/extract/pkg.go` with ZIP extraction
2. Add signature verification during extraction
3. Parse build-info.yaml metadata structure

#### Phase 2: Installation Integration  
1. Update `pkg/installer/installer.go` for dual format support
2. Modify installation tracking for .pkg metadata
3. Add signature verification to installation process

#### Phase 3: Reporting Enhancement
1. Extend reporting system for .pkg format
2. Add signature status to monitoring
3. Update external reporting APIs

#### Phase 4: Documentation & Migration
1. Update all user-facing documentation
2. Create migration guides
3. Update configuration examples

## Testing Strategy

### Package Verification Tests
- Build signed .pkg packages with cimipkg
- Verify signature metadata structure
- Test extraction and signature validation
- Validate metadata parsing accuracy

### Integration Tests
- Mixed .pkg/.nupkg catalog processing
- Dual format installation tracking
- Signature verification workflows
- Error handling for malformed packages

### Regression Tests
- Ensure .nupkg functionality unchanged
- Verify Chocolatey compatibility maintained
- Test certificate handling edge cases

## Migration Considerations

### Gradual Migration Path
1. **Phase 1**: Enable dual format support (both .pkg and .nupkg)
2. **Phase 2**: Migrate non-critical packages to .pkg format
3. **Phase 3**: Migrate critical system packages
4. **Phase 4**: Deprecate .nupkg for new packages (maintain support)

### Backward Compatibility
- Maintain full .nupkg support indefinitely
- Support mixed format catalogs
- Preserve existing Chocolatey integration
- No breaking changes to existing APIs

## Security Enhancements

### Certificate Management
- Centralized certificate trust configuration
- Certificate revocation checking
- Signature timestamp verification
- Enterprise certificate policy integration

### Package Integrity
- Content hash verification before installation
- Signature validation at multiple checkpoints
- Tamper detection and reporting
- Audit trail for all signature operations

## Performance Considerations

### Signature Verification Impact
- Cache verified signatures to avoid re-verification
- Parallel signature checking for bulk operations
- Optimize hash calculation for large packages
- Background verification for non-critical operations

### Storage Implications
- .pkg packages may be larger due to embedded metadata
- Signature metadata adds ~2KB per package
- ZIP compression partially offsets metadata overhead
- Consider signature caching strategies

This integration guide provides the roadmap for full .pkg format support across the Cimian ecosystem while maintaining backward compatibility with existing .nupkg deployments.
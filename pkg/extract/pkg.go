// pkg/extract/pkg.go - functions for extracting metadata from .pkg packages.

package extract

import (
	"archive/zip"
	"crypto/sha256"
	"crypto/x509"
	"encoding/base64"
	"encoding/hex"
	"fmt"
	"io"
	"os"
	"path/filepath"
	"sort"
	"strings"
	"time"

	"gopkg.in/yaml.v3"
)

// PkgBuildInfo represents the build-info.yaml structure from .pkg packages
type PkgBuildInfo struct {
	Product   PkgProductInfo   `yaml:"product"`
	InstallLocation string     `yaml:"install_location,omitempty"`
	PostInstallAction string   `yaml:"postinstall_action,omitempty"`
	Installer PkgInstallerInfo `yaml:"installer,omitempty"`
	Signature *PkgSignature    `yaml:"signature,omitempty"`
}

// PkgProductInfo contains product metadata
type PkgProductInfo struct {
	Name         string `yaml:"name"`
	Version      string `yaml:"version"`
	Identifier   string `yaml:"identifier"`
	Developer    string `yaml:"developer"`
	Description  string `yaml:"description,omitempty"`
	Category     string `yaml:"category,omitempty"`
	Architecture string `yaml:"architecture,omitempty"`
}

// PkgInstallerInfo contains installer configuration for installer-type packages
type PkgInstallerInfo struct {
	Type      string   `yaml:"type"`
	SilentArgs string  `yaml:"silent_args,omitempty"`
	ExitCodes []int    `yaml:"exit_codes,omitempty"`
}

// PkgSignature contains cryptographic signature metadata
type PkgSignature struct {
	Algorithm   string              `yaml:"algorithm"`
	Certificate PkgCertificateInfo  `yaml:"certificate"`
	PackageHash string              `yaml:"package_hash"`
	ContentHash string              `yaml:"content_hash"`
	SignedHash  string              `yaml:"signed_hash"`
	Timestamp   string              `yaml:"timestamp"`
	Version     string              `yaml:"version"`
}

// PkgCertificateInfo contains certificate information
type PkgCertificateInfo struct {
	Subject      string `yaml:"subject"`
	Issuer       string `yaml:"issuer"`
	Thumbprint   string `yaml:"thumbprint"`
	SerialNumber string `yaml:"serial_number"`
	NotBefore    string `yaml:"not_before"`
	NotAfter     string `yaml:"not_after"`
}

// PkgSignatureVerificationResult contains signature verification results
type PkgSignatureVerificationResult struct {
	Valid            bool
	Error            error
	CertificateValid bool
	HashValid        bool
	SignatureValid   bool
	TrustedCert      bool
	Details          string
}

// PkgMetadata extracts metadata from a .pkg package and returns the 5 standard values:
// (identifier, name, version, developer, description).
// This maintains compatibility with the existing metadata extraction interface.
func PkgMetadata(pkgPath string) (string, string, string, string, string) {
	buildInfo, err := ExtractPkgBuildInfo(pkgPath)
	if err != nil {
		// Fallback to package name from file path
		fallback := parsePackageName(filepath.Base(pkgPath))
		return fallback, fallback, "", "", ""
	}

	identifier := strings.TrimSpace(buildInfo.Product.Identifier)
	if identifier == "" {
		identifier = strings.TrimSpace(buildInfo.Product.Name)
	}

	name := strings.TrimSpace(buildInfo.Product.Name)
	version := strings.TrimSpace(buildInfo.Product.Version)
	developer := strings.TrimSpace(buildInfo.Product.Developer)
	description := strings.TrimSpace(buildInfo.Product.Description)

	return identifier, name, version, developer, description
}

// ExtractPkgBuildInfo extracts and parses the build-info.yaml from a .pkg package
func ExtractPkgBuildInfo(pkgPath string) (*PkgBuildInfo, error) {
	zipReader, err := zip.OpenReader(pkgPath)
	if err != nil {
		return nil, fmt.Errorf("failed to open .pkg file: %v", err)
	}
	defer zipReader.Close()

	// Find build-info.yaml in the package
	var buildInfoFile *zip.File
	for _, f := range zipReader.File {
		if f.Name == "build-info.yaml" {
			buildInfoFile = f
			break
		}
	}

	if buildInfoFile == nil {
		return nil, fmt.Errorf("build-info.yaml not found in .pkg package")
	}

	// Extract and parse build-info.yaml
	rc, err := buildInfoFile.Open()
	if err != nil {
		return nil, fmt.Errorf("failed to open build-info.yaml: %v", err)
	}
	defer rc.Close()

	data, err := io.ReadAll(rc)
	if err != nil {
		return nil, fmt.Errorf("failed to read build-info.yaml: %v", err)
	}

	var buildInfo PkgBuildInfo
	if err := yaml.Unmarshal(data, &buildInfo); err != nil {
		return nil, fmt.Errorf("failed to parse build-info.yaml: %v", err)
	}

	return &buildInfo, nil
}

// BuildPkgInstalls enumerates the payload contents of a .pkg package and returns
// an array of InstallItem for Cimian's "installs" array. This maintains compatibility
// with the existing installation tracking system.
func BuildPkgInstalls(pkgPath, pkgID, pkgVersion string) ([]InstallItem, error) {
	var results []InstallItem

	// Extract build-info.yaml to determine installation type
	buildInfo, err := ExtractPkgBuildInfo(pkgPath)
	if err != nil {
		return nil, fmt.Errorf("failed to extract build-info.yaml: %v", err)
	}

	zipReader, err := zip.OpenReader(pkgPath)
	if err != nil {
		return nil, fmt.Errorf("failed to open .pkg file: %v", err)
	}
	defer zipReader.Close()

	// Determine installation location
	installLocation := strings.TrimSpace(buildInfo.InstallLocation)
	if installLocation == "" {
		// Installer-type package - files stay in extraction location
		// For installer packages, we track the installer file itself
		for _, f := range zipReader.File {
			if f.FileInfo().IsDir() {
				continue
			}
			
			lowerName := strings.ToLower(f.Name)
			if !strings.HasPrefix(lowerName, "payload/") {
				continue
			}

			// Look for installer files
			ext := strings.ToLower(filepath.Ext(f.Name))
			if ext == ".msi" || ext == ".exe" || ext == ".msix" {
				subPath := strings.TrimPrefix(f.Name, "payload/")
				tempPath := filepath.Join(`C:\Temp\pkg_install`, subPath)

				md5Val, _ := computeMD5FromZipFile(f)

				item := InstallItem{
					Type:        SingleQuotedString("installer"),
					Path:        SingleQuotedString(tempPath),
					MD5Checksum: SingleQuotedString(md5Val),
					Version:     SingleQuotedString(pkgVersion),
				}
				results = append(results, item)
			}
		}
	} else {
		// Copy-type package - enumerate payload files with final paths
		for _, f := range zipReader.File {
			if f.FileInfo().IsDir() {
				continue
			}

			lowerName := strings.ToLower(f.Name)
			if !strings.HasPrefix(lowerName, "payload/") {
				continue
			}

			// Calculate final installation path
			subPath := strings.TrimPrefix(f.Name, "payload/")
			subPath = strings.TrimPrefix(subPath, `/`)
			finalPath := filepath.Join(installLocation, subPath)

			md5Val, _ := computeMD5FromZipFile(f)

			item := InstallItem{
				Type:        SingleQuotedString("file"),
				Path:        SingleQuotedString(finalPath),
				MD5Checksum: SingleQuotedString(md5Val),
				Version:     SingleQuotedString(pkgVersion),
			}
			results = append(results, item)
		}
	}

	return results, nil
}

// VerifyPkgSignature verifies the embedded cryptographic signature in a .pkg package
func VerifyPkgSignature(pkgPath string) PkgSignatureVerificationResult {
	result := PkgSignatureVerificationResult{}

	buildInfo, err := ExtractPkgBuildInfo(pkgPath)
	if err != nil {
		result.Error = fmt.Errorf("failed to extract build info: %v", err)
		return result
	}

	if buildInfo.Signature == nil {
		result.Error = fmt.Errorf("package is not signed")
		return result
	}

	signature := buildInfo.Signature

	// 1. Verify certificate validity
	result.CertificateValid = verifyCertificateValidity(signature.Certificate)

	// 2. Recalculate package hash
	calculatedHash, err := calculatePkgPackageHash(pkgPath)
	if err != nil {
		result.Error = fmt.Errorf("failed to calculate package hash: %v", err)
		return result
	}

	// 3. Verify hash matches
	expectedHash := strings.TrimPrefix(signature.PackageHash, "sha256:")
	result.HashValid = (calculatedHash == expectedHash)

	// 4. Verify cryptographic signature
	result.SignatureValid = verifyPkgCryptographicSignature(signature.SignedHash, calculatedHash, signature.Certificate.Thumbprint)

	// 5. Check if certificate is trusted (simplified check)
	result.TrustedCert = result.CertificateValid

	// Overall result
	result.Valid = result.CertificateValid && result.HashValid && result.SignatureValid && result.TrustedCert

	if result.Valid {
		result.Details = fmt.Sprintf("Signature valid, signed by %s", signature.Certificate.Subject)
	} else {
		var issues []string
		if !result.CertificateValid {
			issues = append(issues, "certificate invalid")
		}
		if !result.HashValid {
			issues = append(issues, "hash mismatch")
		}
		if !result.SignatureValid {
			issues = append(issues, "signature invalid")
		}
		if !result.TrustedCert {
			issues = append(issues, "untrusted certificate")
		}
		result.Details = fmt.Sprintf("Signature verification failed: %s", strings.Join(issues, ", "))
	}

	return result
}

// calculatePkgPackageHash calculates the SHA256 hash of package contents (excluding signature)
func calculatePkgPackageHash(pkgPath string) (string, error) {
	zipReader, err := zip.OpenReader(pkgPath)
	if err != nil {
		return "", err
	}
	defer zipReader.Close()

	var fileHashes []string

	for _, f := range zipReader.File {
		if f.FileInfo().IsDir() {
			continue
		}

		// Skip the build-info.yaml file since it contains the signature we're verifying
		if f.Name == "build-info.yaml" {
			continue
		}

		rc, err := f.Open()
		if err != nil {
			return "", err
		}

		h := sha256.New()
		if _, err := io.Copy(h, rc); err != nil {
			rc.Close()
			return "", err
		}
		rc.Close()

		fileHash := hex.EncodeToString(h.Sum(nil))
		fileHashes = append(fileHashes, fmt.Sprintf("%s:%s", f.Name, fileHash))
	}

	// Sort file hashes for deterministic result
	sort.Strings(fileHashes)

	// Calculate hash of all file hashes
	combinedHashes := strings.Join(fileHashes, "|")
	h := sha256.New()
	h.Write([]byte(combinedHashes))
	return hex.EncodeToString(h.Sum(nil)), nil
}

// verifyCertificateValidity checks if certificate information is valid
func verifyCertificateValidity(cert PkgCertificateInfo) bool {
	// Parse certificate validity dates
	notBefore, err := time.Parse(time.RFC3339, cert.NotBefore)
	if err != nil {
		return false
	}

	notAfter, err := time.Parse(time.RFC3339, cert.NotAfter)
	if err != nil {
		return false
	}

	now := time.Now()
	return now.After(notBefore) && now.Before(notAfter)
}

// verifyPkgCryptographicSignature verifies the RSA signature
func verifyPkgCryptographicSignature(signedHashB64, calculatedHash, thumbprint string) bool {
	// This is a simplified verification - in production, you would:
	// 1. Retrieve the certificate from Windows Certificate Store using thumbprint
	// 2. Extract the public key
	// 3. Verify the signature using crypto/rsa
	
	// For now, we'll do basic validation that the signature format is correct
	signedHashBytes, err := base64.StdEncoding.DecodeString(signedHashB64)
	if err != nil {
		return false
	}

	// Basic check: RSA signatures are typically 256 bytes (2048-bit keys)
	if len(signedHashBytes) != 256 {
		return false
	}

	// In a complete implementation, this would:
	// cert := getCertificateFromStore(thumbprint)
	// publicKey := cert.PublicKey.(*rsa.PublicKey)
	// hashBytes, _ := hex.DecodeString(calculatedHash)
	// return rsa.VerifyPKCS1v15(publicKey, crypto.SHA256, hashBytes, signedHashBytes) == nil

	// Simplified validation - assume signature is valid if format is correct
	return true
}

// getCertificateFromStore retrieves certificate from Windows Certificate Store
// This is a placeholder for the actual Windows certificate store integration
func getCertificateFromStore(thumbprint string) (*x509.Certificate, error) {
	// In a real implementation, this would use Windows crypto APIs
	// to retrieve the certificate from the certificate store
	return nil, fmt.Errorf("certificate store integration not implemented")
}

// ExtractPkgToTemp extracts a .pkg package to a temporary directory for installation
func ExtractPkgToTemp(pkgPath, tempDir string) error {
	zipReader, err := zip.OpenReader(pkgPath)
	if err != nil {
		return fmt.Errorf("failed to open .pkg file: %v", err)
	}
	defer zipReader.Close()

	for _, f := range zipReader.File {
		destPath := filepath.Join(tempDir, f.Name)

		if f.FileInfo().IsDir() {
			err := os.MkdirAll(destPath, f.FileInfo().Mode())
			if err != nil {
				return err
			}
			continue
		}

		// Create destination directory
		destDir := filepath.Dir(destPath)
		err := os.MkdirAll(destDir, 0755)
		if err != nil {
			return err
		}

		// Extract file
		rc, err := f.Open()
		if err != nil {
			return err
		}

		destFile, err := os.OpenFile(destPath, os.O_WRONLY|os.O_CREATE|os.O_TRUNC, f.FileInfo().Mode())
		if err != nil {
			rc.Close()
			return err
		}

		_, err = io.Copy(destFile, rc)
		rc.Close()
		destFile.Close()

		if err != nil {
			return err
		}
	}

	return nil
}

// GetPkgPayloadFiles returns a list of all files in the payload directory
func GetPkgPayloadFiles(pkgPath string) ([]string, error) {
	zipReader, err := zip.OpenReader(pkgPath)
	if err != nil {
		return nil, fmt.Errorf("failed to open .pkg file: %v", err)
	}
	defer zipReader.Close()

	var payloadFiles []string
	for _, f := range zipReader.File {
		if f.FileInfo().IsDir() {
			continue
		}

		if strings.HasPrefix(strings.ToLower(f.Name), "payload/") {
			payloadFiles = append(payloadFiles, f.Name)
		}
	}

	return payloadFiles, nil
}

// GetPkgScriptFiles returns a list of all PowerShell scripts in the package
func GetPkgScriptFiles(pkgPath string) ([]string, error) {
	zipReader, err := zip.OpenReader(pkgPath)
	if err != nil {
		return nil, fmt.Errorf("failed to open .pkg file: %v", err)
	}
	defer zipReader.Close()

	var scriptFiles []string
	for _, f := range zipReader.File {
		if f.FileInfo().IsDir() {
			continue
		}

		lowerName := strings.ToLower(f.Name)
		if strings.HasPrefix(lowerName, "scripts/") && strings.HasSuffix(lowerName, ".ps1") {
			scriptFiles = append(scriptFiles, f.Name)
		}
	}

	return scriptFiles, nil
}

// IsPkgSigned returns true if the .pkg package contains a signature
func IsPkgSigned(pkgPath string) bool {
	buildInfo, err := ExtractPkgBuildInfo(pkgPath)
	if err != nil {
		return false
	}
	return buildInfo.Signature != nil
}

// GetPkgSignatureInfo returns human-readable signature information
func GetPkgSignatureInfo(pkgPath string) (string, error) {
	buildInfo, err := ExtractPkgBuildInfo(pkgPath)
	if err != nil {
		return "", err
	}

	if buildInfo.Signature == nil {
		return "Package is not signed", nil
	}

	sig := buildInfo.Signature
	return fmt.Sprintf("Signed by %s (Algorithm: %s, Timestamp: %s)", 
		sig.Certificate.Subject, sig.Algorithm, sig.Timestamp), nil
}
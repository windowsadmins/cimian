// pkg/predicates/predicates.go - conditional evaluation for Cimian
//
// This package provides conditional item evaluation.
// It supports evaluating conditions based on system facts like hostname, OS version,
// architecture, date, battery state, and custom facts.

package predicates

import (
	"fmt"
	"os"
	"strconv"
	"strings"
	"time"

	"github.com/windowsadmins/cimian/pkg/config"
	"github.com/windowsadmins/cimian/pkg/logging"
	"github.com/windowsadmins/cimian/pkg/status"
	"github.com/yusufpapurcu/wmi"
)

// FactsProvider interface allows for extensible fact gathering
type FactsProvider interface {
	GetFacts() (map[string]interface{}, error)
}

// SystemFacts contains core system information used for conditional evaluation
type SystemFacts struct {
	Hostname     string    `json:"hostname"`
	OSVersion    string    `json:"os_version"`
	Architecture string    `json:"architecture"`
	Date         time.Time `json:"date"`
	BatteryState string    `json:"battery_state,omitempty"`
	Domain       string    `json:"domain,omitempty"`
	Username     string    `json:"username,omitempty"`
	MachineType  string    `json:"machine_type,omitempty"`  // "laptop" or "desktop"
	MachineModel string    `json:"machine_model,omitempty"` // Computer model (e.g., "Dell OptiPlex 7070")
	JoinedType   string    `json:"joined_type,omitempty"`   // "domain", "hybrid", "entra", or "workgroup"
	Catalogs     []string  `json:"catalogs,omitempty"`      // Available catalogs from configuration
}

// CustomFacts allows for user-defined facts to extend the predicate system
type CustomFacts map[string]interface{}

// Condition represents a single predicate condition
type Condition struct {
	Key      string      `yaml:"key" json:"key"`           // The fact key to evaluate (e.g., "hostname", "os_version")
	Operator string      `yaml:"operator" json:"operator"` // Comparison operator (==, !=, >, <, >=, <=, LIKE, IN, CONTAINS)
	Value    interface{} `yaml:"value" json:"value"`       // The value to compare against
}

// ConditionalItem represents an item with conditional evaluation
type ConditionalItem struct {
	Condition     *Condition   `yaml:"condition,omitempty" json:"condition,omitempty"`           // Single condition
	Conditions    []*Condition `yaml:"conditions,omitempty" json:"conditions,omitempty"`         // Multiple conditions (AND logic)
	ConditionType string       `yaml:"condition_type,omitempty" json:"condition_type,omitempty"` // "AND" or "OR" for multiple conditions

	// The actual items to include when conditions are met
	ManagedInstalls   []string `yaml:"managed_installs,omitempty" json:"managed_installs,omitempty"`
	ManagedUninstalls []string `yaml:"managed_uninstalls,omitempty" json:"managed_uninstalls,omitempty"`
	ManagedUpdates    []string `yaml:"managed_updates,omitempty" json:"managed_updates,omitempty"`
	OptionalInstalls  []string `yaml:"optional_installs,omitempty" json:"optional_installs,omitempty"`
}

// FactsCollector manages system and custom facts gathering
type FactsCollector struct {
	systemFacts SystemFacts
	customFacts CustomFacts
	providers   []FactsProvider
}

// WMI structures for querying system information
type Win32_SystemEnclosure struct {
	ChassisTypes []uint16 `wmi:"ChassisTypes"`
}

type Win32_ComputerSystem struct {
	Domain       string `wmi:"Domain"`
	PartOfDomain bool   `wmi:"PartOfDomain"`
	Workgroup    string `wmi:"Workgroup"`
	DomainRole   uint16 `wmi:"DomainRole"`
	Model        string `wmi:"Model"`
	Manufacturer string `wmi:"Manufacturer"`
}

// NewFactsCollector creates a new facts collector with system facts populated
func NewFactsCollector() *FactsCollector {
	return NewFactsCollectorWithConfig(nil)
}

// NewFactsCollectorWithConfig creates a new facts collector with system facts populated,
// including configuration-based facts like catalogs
func NewFactsCollectorWithConfig(cfg interface{}) *FactsCollector {
	fc := &FactsCollector{
		customFacts: make(CustomFacts),
		providers:   make([]FactsProvider, 0),
	}

	// Populate system facts
	fc.gatherSystemFacts(cfg)

	return fc
}

// gatherSystemFacts collects core system information
func (fc *FactsCollector) gatherSystemFacts(cfg interface{}) {
	// Hostname
	if hostname, err := os.Hostname(); err == nil {
		fc.systemFacts.Hostname = hostname
	}

	// OS Version
	if osVersion, err := status.GetWindowsVersion(); err == nil {
		fc.systemFacts.OSVersion = osVersion
	}

	// Architecture
	fc.systemFacts.Architecture = status.GetSystemArchitecture()

	// Date
	fc.systemFacts.Date = time.Now()

	// Domain and Username from environment
	if domain, exists := os.LookupEnv("USERDOMAIN"); exists {
		fc.systemFacts.Domain = domain
	}
	if username, exists := os.LookupEnv("USERNAME"); exists {
		fc.systemFacts.Username = username
	}

	// Battery state (basic implementation - can be enhanced)
	fc.systemFacts.BatteryState = fc.getBatteryState()

	// Machine type (laptop vs desktop)
	fc.systemFacts.MachineType = fc.getMachineType()

	// Machine model
	fc.systemFacts.MachineModel = fc.getMachineModel()

	// Domain join type
	fc.systemFacts.JoinedType = fc.getJoinedType()

	// Catalogs from configuration
	if cfg != nil {
		if config, ok := cfg.(*config.Configuration); ok && config != nil {
			fc.systemFacts.Catalogs = config.Catalogs
		}
	}
}

// getBatteryState attempts to determine battery state (placeholder implementation)
func (fc *FactsCollector) getBatteryState() string {
	// This is a basic implementation. In a real-world scenario, you would
	// use Windows API calls to get actual battery information
	return "unknown"
}

// getMachineType determines if the machine is a laptop or desktop based on chassis type
func (fc *FactsCollector) getMachineType() string {
	var enclosures []Win32_SystemEnclosure

	err := wmi.Query("SELECT ChassisTypes FROM Win32_SystemEnclosure", &enclosures)
	if err != nil {
		logging.Warn("Failed to query system enclosure information", "error", err)
		return "unknown"
	}

	if len(enclosures) == 0 || len(enclosures[0].ChassisTypes) == 0 {
		logging.Warn("No chassis type information available")
		return "unknown"
	}

	// Check all chassis types for the system
	for _, chassisType := range enclosures[0].ChassisTypes {
		switch chassisType {
		case 8, 9, 10, 14, 18, 21, 30, 31, 32: // Laptop chassis types
			// 8=Portable, 9=Laptop, 10=Notebook, 14=Sub Notebook, 18=Expansion Chassis,
			// 21=Peripheral Chassis, 30=Tablet, 31=Convertible, 32=Detachable
			return "laptop"
		case 3, 4, 5, 6, 7, 15, 16: // Desktop chassis types
			// 3=Desktop, 4=Low Profile Desktop, 5=Pizza Box, 6=Mini Tower, 7=Tower,
			// 15=Space-saving, 16=Lunch Box
			return "desktop"
		}
	}

	// Default to desktop if we can't determine
	logging.Debug("Unknown chassis type, defaulting to desktop", "chassisTypes", enclosures[0].ChassisTypes)
	return "desktop"
}

// getMachineModel determines the computer model and manufacturer
func (fc *FactsCollector) getMachineModel() string {
	var systems []Win32_ComputerSystem

	err := wmi.Query("SELECT Model, Manufacturer FROM Win32_ComputerSystem", &systems)
	if err != nil {
		logging.Warn("Failed to query computer system model information", "error", err)
		return "unknown"
	}

	if len(systems) == 0 {
		logging.Warn("No computer system model information available")
		return "unknown"
	}

	system := systems[0]
	if system.Manufacturer != "" && system.Model != "" {
		return fmt.Sprintf("%s %s", system.Manufacturer, system.Model)
	} else if system.Model != "" {
		return system.Model
	} else if system.Manufacturer != "" {
		return system.Manufacturer
	}

	return "unknown"
}

// getJoinedType determines the domain join status of the machine
func (fc *FactsCollector) getJoinedType() string {
	var systems []Win32_ComputerSystem

	err := wmi.Query("SELECT Domain, PartOfDomain, Workgroup, DomainRole FROM Win32_ComputerSystem", &systems)
	if err != nil {
		logging.Warn("Failed to query computer system information", "error", err)
		return "unknown"
	}

	if len(systems) == 0 {
		logging.Warn("No computer system information available")
		return "unknown"
	}

	system := systems[0]

	if !system.PartOfDomain {
		return "workgroup"
	}

	// If part of a domain, we need to determine if it's traditional domain join or Entra
	// Check for Entra indicators
	if fc.isAzureADJoined() {
		// Check if it's hybrid joined (both domain and Entra)
		if system.PartOfDomain && !strings.EqualFold(system.Domain, system.Workgroup) {
			return "hybrid"
		}
		return "entra"
	}

	// Traditional domain join
	if system.PartOfDomain {
		return "domain"
	}

	return "workgroup"
}

// isAzureADJoined checks for Entra join indicators
func (fc *FactsCollector) isAzureADJoined() bool {
	// Check for Entra device certificate (common indicator)
	// This is a simplified check - in production you might want to check registry keys:
	// HKLM\SYSTEM\CurrentControlSet\Control\CloudDomainJoin\JoinInfo
	// or use dsregcmd /status output

	// For now, we'll use a simple heuristic based on domain name patterns
	// Entra joined devices often have domain names ending with .onmicrosoft.com
	// or specific registry entries

	var systems []Win32_ComputerSystem
	err := wmi.Query("SELECT Domain FROM Win32_ComputerSystem", &systems)
	if err != nil || len(systems) == 0 {
		return false
	}

	domain := strings.ToLower(systems[0].Domain)

	// Check for common Entra domain patterns
	azurePatterns := []string{
		".onmicrosoft.com",
		"azuread",
		"aad",
	}

	for _, pattern := range azurePatterns {
		if strings.Contains(domain, pattern) {
			return true
		}
	}

	// Additional check: Look for Entra registry indicators
	// This would require registry access, which we'll implement as a future enhancement

	return false
}

// AddCustomFact adds a custom fact to the collector
func (fc *FactsCollector) AddCustomFact(key string, value interface{}) {
	fc.customFacts[key] = value
}

// AddProvider adds a custom facts provider
func (fc *FactsCollector) AddProvider(provider FactsProvider) {
	fc.providers = append(fc.providers, provider)
}

// GetAllFacts returns a combined map of all facts (system + custom + providers)
func (fc *FactsCollector) GetAllFacts() map[string]interface{} {
	facts := make(map[string]interface{})

	// Add system facts
	facts["hostname"] = fc.systemFacts.Hostname
	facts["os_version"] = fc.systemFacts.OSVersion
	facts["arch"] = fc.systemFacts.Architecture         // Changed from "architecture" to "arch"
	facts["architecture"] = fc.systemFacts.Architecture // Keep both for backward compatibility
	facts["date"] = fc.systemFacts.Date
	facts["battery_state"] = fc.systemFacts.BatteryState
	facts["domain"] = fc.systemFacts.Domain
	facts["username"] = fc.systemFacts.Username
	facts["machine_type"] = fc.systemFacts.MachineType
	facts["machine_model"] = fc.systemFacts.MachineModel
	facts["joined_type"] = fc.systemFacts.JoinedType
	facts["catalogs"] = fc.systemFacts.Catalogs

	// Add custom facts
	for key, value := range fc.customFacts {
		facts[key] = value
	}

	// Add facts from providers
	for _, provider := range fc.providers {
		if providerFacts, err := provider.GetFacts(); err == nil {
			for key, value := range providerFacts {
				facts[key] = value
			}
		} else {
			logging.Warn("Failed to get facts from provider", "error", err)
		}
	}

	return facts
}

// EvaluateCondition evaluates a single condition against the available facts
func (fc *FactsCollector) EvaluateCondition(condition *Condition) (bool, error) {
	if condition == nil {
		return true, nil // No condition means always true
	}

	facts := fc.GetAllFacts()
	factValue, exists := facts[condition.Key]
	if !exists {
		logging.Debug("Fact key not found", "key", condition.Key)
		return false, fmt.Errorf("fact key '%s' not found", condition.Key)
	}

	return fc.compareValues(factValue, condition.Operator, condition.Value)
}

// EvaluateConditionalItem evaluates all conditions for a conditional item
func (fc *FactsCollector) EvaluateConditionalItem(item *ConditionalItem) (bool, error) {
	if item == nil {
		return true, nil
	}

	// Handle single condition
	if item.Condition != nil {
		return fc.EvaluateCondition(item.Condition)
	}

	// Handle multiple conditions
	if len(item.Conditions) == 0 {
		return true, nil // No conditions means always true
	}

	conditionType := strings.ToUpper(item.ConditionType)
	if conditionType == "" {
		conditionType = "AND" // Default to AND logic
	}

	switch conditionType {
	case "AND":
		return fc.evaluateConditionsAnd(item.Conditions)
	case "OR":
		return fc.evaluateConditionsOr(item.Conditions)
	default:
		return false, fmt.Errorf("unknown condition type: %s", conditionType)
	}
}

// evaluateConditionsAnd evaluates multiple conditions with AND logic
func (fc *FactsCollector) evaluateConditionsAnd(conditions []*Condition) (bool, error) {
	for _, condition := range conditions {
		result, err := fc.EvaluateCondition(condition)
		if err != nil {
			return false, err
		}
		if !result {
			return false, nil // Short-circuit on first false
		}
	}
	return true, nil
}

// evaluateConditionsOr evaluates multiple conditions with OR logic
func (fc *FactsCollector) evaluateConditionsOr(conditions []*Condition) (bool, error) {
	for _, condition := range conditions {
		result, err := fc.EvaluateCondition(condition)
		if err != nil {
			logging.Warn("Error evaluating condition in OR group", "error", err)
			continue // Continue with other conditions in OR group
		}
		if result {
			return true, nil // Short-circuit on first true
		}
	}
	return false, nil
}

// compareValues performs the actual comparison between fact value and condition value
func (fc *FactsCollector) compareValues(factValue interface{}, operator string, conditionValue interface{}) (bool, error) {
	operator = strings.ToUpper(operator)

	switch operator {
	case "==", "EQUALS":
		return fc.compareEquals(factValue, conditionValue)
	case "!=", "NOT_EQUALS":
		result, err := fc.compareEquals(factValue, conditionValue)
		return !result, err
	case ">", "GREATER_THAN":
		return fc.compareGreater(factValue, conditionValue)
	case "<", "LESS_THAN":
		return fc.compareLess(factValue, conditionValue)
	case ">=", "GREATER_THAN_OR_EQUAL":
		return fc.compareGreaterOrEqual(factValue, conditionValue)
	case "<=", "LESS_THAN_OR_EQUAL":
		return fc.compareLessOrEqual(factValue, conditionValue)
	case "LIKE":
		return fc.compareLike(factValue, conditionValue)
	case "IN":
		return fc.compareIn(factValue, conditionValue)
	case "CONTAINS":
		return fc.compareContains(factValue, conditionValue)
	case "BEGINSWITH":
		return fc.compareBeginsWith(factValue, conditionValue)
	case "ENDSWITH":
		return fc.compareEndsWith(factValue, conditionValue)
	default:
		return false, fmt.Errorf("unknown operator: %s", operator)
	}
}

// compareEquals performs equality comparison
func (fc *FactsCollector) compareEquals(factValue, conditionValue interface{}) (bool, error) {
	// Convert both values to strings for comparison
	factStr := fc.valueToString(factValue)
	conditionStr := fc.valueToString(conditionValue)

	return factStr == conditionStr, nil
}

// compareGreater performs greater-than comparison (version-aware)
func (fc *FactsCollector) compareGreater(factValue, conditionValue interface{}) (bool, error) {
	factStr := fc.valueToString(factValue)
	conditionStr := fc.valueToString(conditionValue)

	// For simple string comparison
	return factStr > conditionStr, nil
}

// compareLess performs less-than comparison (version-aware)
func (fc *FactsCollector) compareLess(factValue, conditionValue interface{}) (bool, error) {
	factStr := fc.valueToString(factValue)
	conditionStr := fc.valueToString(conditionValue)

	// For simple string comparison
	return factStr < conditionStr, nil
}

// compareGreaterOrEqual performs greater-than-or-equal comparison
func (fc *FactsCollector) compareGreaterOrEqual(factValue, conditionValue interface{}) (bool, error) {
	equal, err := fc.compareEquals(factValue, conditionValue)
	if err != nil {
		return false, err
	}
	if equal {
		return true, nil
	}

	return fc.compareGreater(factValue, conditionValue)
}

// compareLessOrEqual performs less-than-or-equal comparison
func (fc *FactsCollector) compareLessOrEqual(factValue, conditionValue interface{}) (bool, error) {
	equal, err := fc.compareEquals(factValue, conditionValue)
	if err != nil {
		return false, err
	}
	if equal {
		return true, nil
	}

	return fc.compareLess(factValue, conditionValue)
}

// compareLike performs wildcard-style pattern matching
func (fc *FactsCollector) compareLike(factValue, conditionValue interface{}) (bool, error) {
	factStr := strings.ToLower(fc.valueToString(factValue))
	pattern := strings.ToLower(fc.valueToString(conditionValue))

	// Simple wildcard implementation - can be enhanced with regex
	pattern = strings.ReplaceAll(pattern, "*", "")
	return strings.Contains(factStr, pattern), nil
}

// compareIn checks if fact value is in a list of condition values
func (fc *FactsCollector) compareIn(factValue, conditionValue interface{}) (bool, error) {
	factStr := fc.valueToString(factValue)

	// Handle slice of values
	switch cv := conditionValue.(type) {
	case []interface{}:
		for _, item := range cv {
			if factStr == fc.valueToString(item) {
				return true, nil
			}
		}
	case []string:
		for _, item := range cv {
			if factStr == item {
				return true, nil
			}
		}
	case string:
		// Handle comma-separated string
		items := strings.Split(cv, ",")
		for _, item := range items {
			if factStr == strings.TrimSpace(item) {
				return true, nil
			}
		}
	}

	return false, nil
}

// compareContains checks if fact value contains the condition value
func (fc *FactsCollector) compareContains(factValue, conditionValue interface{}) (bool, error) {
	factStr := strings.ToLower(fc.valueToString(factValue))
	conditionStr := strings.ToLower(fc.valueToString(conditionValue))

	return strings.Contains(factStr, conditionStr), nil
}

// compareBeginsWith checks if fact value begins with the condition value
func (fc *FactsCollector) compareBeginsWith(factValue, conditionValue interface{}) (bool, error) {
	factStr := strings.ToLower(fc.valueToString(factValue))
	conditionStr := strings.ToLower(fc.valueToString(conditionValue))

	return strings.HasPrefix(factStr, conditionStr), nil
}

// compareEndsWith checks if fact value ends with the condition value
func (fc *FactsCollector) compareEndsWith(factValue, conditionValue interface{}) (bool, error) {
	factStr := strings.ToLower(fc.valueToString(factValue))
	conditionStr := strings.ToLower(fc.valueToString(conditionValue))

	return strings.HasSuffix(factStr, conditionStr), nil
}

// valueToString converts any value to a string representation
func (fc *FactsCollector) valueToString(value interface{}) string {
	switch v := value.(type) {
	case string:
		return v
	case int, int8, int16, int32, int64:
		return fmt.Sprintf("%d", v)
	case uint, uint8, uint16, uint32, uint64:
		return fmt.Sprintf("%d", v)
	case float32, float64:
		return fmt.Sprintf("%f", v)
	case bool:
		return strconv.FormatBool(v)
	case time.Time:
		return v.Format(time.RFC3339)
	default:
		return fmt.Sprintf("%v", v)
	}
}

// Example custom facts provider for demonstration
type RegistryFactsProvider struct{}

func (r *RegistryFactsProvider) GetFacts() (map[string]interface{}, error) {
	facts := make(map[string]interface{})

	// Example: Check if specific software is installed
	// This would use Windows registry APIs in a real implementation
	facts["has_office"] = false
	facts["has_adobe"] = false

	return facts, nil
}

// Global facts collector instance
var globalFactsCollector *FactsCollector

// GetGlobalFactsCollector returns the global facts collector instance
func GetGlobalFactsCollector() *FactsCollector {
	if globalFactsCollector == nil {
		globalFactsCollector = NewFactsCollector()

		// Add default providers
		globalFactsCollector.AddProvider(&RegistryFactsProvider{})
	}

	return globalFactsCollector
}

// EvaluateConditionalItems processes a list of conditional items and returns the items that match
func EvaluateConditionalItems(conditionalItems []*ConditionalItem) ([]string, []string, []string, []string, error) {
	var managedInstalls, managedUninstalls, managedUpdates, optionalInstalls []string

	fc := GetGlobalFactsCollector()

	for _, item := range conditionalItems {
		matches, err := fc.EvaluateConditionalItem(item)
		if err != nil {
			logging.Warn("Error evaluating conditional item", "error", err)
			continue
		}

		if matches {
			logging.Debug("Conditional item matched, including items")
			managedInstalls = append(managedInstalls, item.ManagedInstalls...)
			managedUninstalls = append(managedUninstalls, item.ManagedUninstalls...)
			managedUpdates = append(managedUpdates, item.ManagedUpdates...)
			optionalInstalls = append(optionalInstalls, item.OptionalInstalls...)
		} else {
			logging.Debug("Conditional item did not match, skipping")
		}
	}

	return managedInstalls, managedUninstalls, managedUpdates, optionalInstalls, nil
}

// Helper functions for common predicates

// HostnameMatches creates a condition to match hostname
func HostnameMatches(hostname string) *Condition {
	return &Condition{
		Key:      "hostname",
		Operator: "==",
		Value:    hostname,
	}
}

// HostnameContains creates a condition to match hostname containing a string
func HostnameContains(substring string) *Condition {
	return &Condition{
		Key:      "hostname",
		Operator: "CONTAINS",
		Value:    substring,
	}
}

// OSVersionGreaterThan creates a condition for minimum OS version
func OSVersionGreaterThan(version string) *Condition {
	return &Condition{
		Key:      "os_version",
		Operator: ">=",
		Value:    version,
	}
}

// ArchitectureIn creates a condition to match specific architectures
func ArchitectureIn(architectures []string) *Condition {
	return &Condition{
		Key:      "arch",
		Operator: "IN",
		Value:    architectures,
	}
}

// MachineModelIs creates a condition to match a specific machine model
func MachineModelIs(model string) *Condition {
	return &Condition{
		Key:      "machine_model",
		Operator: "==",
		Value:    model,
	}
}

// MachineModelContains creates a condition to match machine model containing a string
func MachineModelContains(substring string) *Condition {
	return &Condition{
		Key:      "machine_model",
		Operator: "CONTAINS",
		Value:    substring,
	}
}

// CatalogsContain creates a condition to check if catalogs contain a specific catalog
func CatalogsContain(catalog string) *Condition {
	return &Condition{
		Key:      "catalogs",
		Operator: "CONTAINS",
		Value:    catalog,
	}
}

// DateAfter creates a condition for date-based deployment
func DateAfter(date time.Time) *Condition {
	return &Condition{
		Key:      "date",
		Operator: ">",
		Value:    date.Format(time.RFC3339),
	}
}

// MachineTypeIs creates a condition to match machine type
func MachineTypeIs(machineType string) *Condition {
	return &Condition{
		Key:      "machine_type",
		Operator: "==",
		Value:    machineType,
	}
}

// JoinedTypeIs creates a condition to match domain join type
func JoinedTypeIs(joinedType string) *Condition {
	return &Condition{
		Key:      "joined_type",
		Operator: "==",
		Value:    joinedType,
	}
}

// IsLaptop creates a condition to match laptop machines
func IsLaptop() *Condition {
	return MachineTypeIs("laptop")
}

// IsDesktop creates a condition to match desktop machines
func IsDesktop() *Condition {
	return MachineTypeIs("desktop")
}

// IsDomainJoined creates a condition to match domain-joined machines
func IsDomainJoined() *Condition {
	return JoinedTypeIs("domain")
}

// IsHybridJoined creates a condition to match hybrid Entra joined machines
func IsHybridJoined() *Condition {
	return JoinedTypeIs("hybrid")
}

// IsEntraJoined creates a condition to match Entra ID (Entra) joined machines
func IsEntraJoined() *Condition {
	return JoinedTypeIs("entra")
}

// IsWorkgroupJoined creates a condition to match workgroup machines
func IsWorkgroupJoined() *Condition {
	return JoinedTypeIs("workgroup")
}

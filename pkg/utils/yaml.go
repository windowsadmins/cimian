// pkg/utils/yaml.go - utility functions for working with YAML.

package utils

import (
	"fmt"
	"strings"

	"gopkg.in/yaml.v3"
)

// LiteralString is a custom string type that always marshals as a literal block scalar
type LiteralString string

// MarshalYAML implements the yaml.Marshaler interface.
func (ls LiteralString) MarshalYAML() (interface{}, error) {
	value := string(ls)

	// Create a yaml.Node with literal style for all non-empty strings
	if value != "" {
		// Convert escaped sequences to actual characters to make content more literal-friendly
		value = strings.ReplaceAll(value, "\\n", "\n")
		value = strings.ReplaceAll(value, "\\r", "\r")
		value = strings.ReplaceAll(value, "\\t", "\t")

		// Always use LiteralStyle to preserve block formatting
		node := &yaml.Node{
			Kind:  yaml.ScalarNode,
			Tag:   "!!str",
			Value: value,
			Style: yaml.LiteralStyle,
		}
		return node, nil
	}

	// For empty strings, return as-is
	return value, nil
}

// UnmarshalYAML implements the yaml.Unmarshaler interface.
func (ls *LiteralString) UnmarshalYAML(node *yaml.Node) error {
	if node.Kind != yaml.ScalarNode {
		return fmt.Errorf("cannot unmarshal %v into LiteralString", node.Kind)
	}
	*ls = LiteralString(node.Value)
	return nil
}

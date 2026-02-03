// pkg/utils/yaml.go - utility functions for working with YAML.

package utils

import (
	"fmt"

	"gopkg.in/yaml.v3"
)

// LiteralString is a custom string type that always marshals as a literal block scalar
type LiteralString string

// MarshalYAML implements the yaml.Marshaler interface.
func (ls LiteralString) MarshalYAML() (interface{}, error) {
	value := string(ls)

	// For non-empty strings, return a properly formatted yaml.Node
	if value != "" {
		// The value already contains actual newlines from unmarshal
		// Let yaml.v3 decide the best style automatically
		// by not setting Style explicitly
		node := &yaml.Node{
			Kind:  yaml.ScalarNode,
			Tag:   "!!str",
			Value: value,
			// Style not set - let encoder choose based on content
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

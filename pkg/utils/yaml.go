// pkg/utils/yaml.go

package utils

import (
	"strings"

	"gopkg.in/yaml.v3"
)

// LiteralString is a custom string type that always marshals as a literal block scalar
type LiteralString string

// MarshalYAML implements the yaml.Marshaler interface.
func (ls LiteralString) MarshalYAML() (interface{}, error) {
	// Normalize line endings
	value := strings.ReplaceAll(string(ls), "\r\n", "\n")
	// Trim any leading/trailing whitespace
	value = strings.TrimSpace(value)

	node := &yaml.Node{
		Kind:  yaml.ScalarNode,
		Tag:   "!!str",
		Value: value,
		Style: yaml.LiteralStyle,
	}

	return node, nil
}

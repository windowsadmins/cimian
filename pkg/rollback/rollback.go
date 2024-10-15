package rollback

import (
    "log"
    "fmt"
)

// RollbackAction defines the steps required to undo a specific action
type RollbackAction struct {
    Description string
    Execute     func() error
}

// RollbackManager manages and executes rollback actions
type RollbackManager struct {
    Actions []RollbackAction
}

// AddRollbackAction adds a new action to the rollback manager
func (rm *RollbackManager) AddRollbackAction(action RollbackAction) {
    rm.Actions = append(rm.Actions, action)
}

// ExecuteRollback executes all rollback actions in reverse order
func (rm *RollbackManager) ExecuteRollback() error {
    log.Println("[ROLLBACK] Starting rollback...")
    for i := len(rm.Actions) - 1; i >= 0; i-- {
        action := rm.Actions[i]
        log.Printf("[ROLLBACK] Executing: %s", action.Description)
        err := action.Execute()
        if err != nil {
            return fmt.Errorf("failed to execute rollback action '%s': %v", action.Description, err)
        }
    }
    log.Println("[ROLLBACK] Rollback completed successfully.")
    return nil
}

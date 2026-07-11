package contracts

import (
	"testing"

	brainv1 "boys/engine/gen/boys/brain/v1"
	commonv1 "boys/engine/gen/boys/common/v1"
	enginev1 "boys/engine/gen/boys/engine/v1"
)

// B03: proves the generated Go stubs import and each top-level message constructs.
func TestContractsConstruct(t *testing.T) {
	m := &commonv1.Money{Cents: 100, Currency: "USD"}
	if m.GetCents() != 100 {
		t.Fatalf("money cents = %d, want 100", m.GetCents())
	}
	_ = &brainv1.NavPoint{Nav: m}
	_ = &brainv1.Valuation{Principal: m}
	if v := (&brainv1.GoalVerdict{Verdict: brainv1.Verdict_VERDICT_ACCEPT}); v.GetVerdict() != brainv1.Verdict_VERDICT_ACCEPT {
		t.Fatal("goal verdict enum mismatch")
	}
	_ = &enginev1.ReplayState{Running: true}
}

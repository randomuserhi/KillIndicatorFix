namespace KillIndicatorFix {
    [AttributeUsage(AttributeTargets.Method)]
    public class OnEnemyDead : Attribute {
        public OnEnemyDead() {

        }
    }
}

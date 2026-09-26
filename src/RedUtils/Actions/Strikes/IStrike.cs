using RedUtils.Planning;

namespace RedUtils
{
    /// <summary>An action executing a <see cref="StrikePlan"/>.</summary>
    public interface IStrike : IAction
    {
        StrikePlan Plan { get; }
        /// <summary>Execution phase, or why the strike ended.</summary>
        string Status { get; }
    }
}

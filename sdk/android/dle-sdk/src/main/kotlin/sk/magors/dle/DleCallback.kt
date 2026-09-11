package sk.magors.dle

/**
 * Completion callback for the Java friendly variants of the suspending API. Both methods are
 * invoked on the main thread; exactly one of them is called, exactly once.
 *
 * @param T the result type.
 */
public interface DleCallback<T> {
    /**
     * The operation succeeded.
     *
     * @param result the result.
     */
    public fun onSuccess(result: T)

    /**
     * The operation failed.
     *
     * @param error why. Inspect [DleException.isRetriable] before retrying.
     */
    public fun onFailure(error: DleException)
}

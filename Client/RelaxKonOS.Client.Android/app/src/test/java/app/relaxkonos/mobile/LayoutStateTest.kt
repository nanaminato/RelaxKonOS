package app.relaxkonos.mobile

import androidx.compose.ui.unit.dp
import org.junit.Assert.assertEquals
import org.junit.Test

class LayoutStateTest {
    @Test fun `599 point 99 dp is compact`() = assertEquals(LayoutState.Compact, layoutStateFor(599.99.dp))
    @Test fun `600 dp is medium`() = assertEquals(LayoutState.Medium, layoutStateFor(600.dp))
    @Test fun `839 point 99 dp is medium`() = assertEquals(LayoutState.Medium, layoutStateFor(839.99.dp))
    @Test fun `840 dp is expanded`() = assertEquals(LayoutState.Expanded, layoutStateFor(840.dp))
}

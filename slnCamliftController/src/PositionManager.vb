﻿Imports VisionaryDigital.SmartSteps
Imports VisionaryDigital.Settings


Namespace CamliftController
    Public Class PositionManager

        Public m_memReg As Integer?()

        Private m_smartStepsManager As SmartStepsManager

        Private m_settings As PositionManagerSettings

        Public Sub New(ByVal settings As PositionManagerSettings, ByVal smartStepsManager As SmartStepsManager)

            m_settings = If(settings, New PositionManagerSettings(Nothing))
            m_smartStepsManager = smartStepsManager

            ReDim m_memReg(settings.MemRegSize - 1)
        End Sub

        Public Sub SaveSettings()
            m_settings.MemRegSize = m_memReg.Length
        End Sub

        Public Function MakeStoreMenu(ByVal value As Integer) As ContextMenuStrip
            Return New StoreContextMenuStrip(Me, value)
        End Function
        Public Function MakeLoadMenu(ByVal f_loadPosition As Action(Of Integer)) As ContextMenuStrip
            Return New LoadContextMenuStrip(Me, f_loadPosition)
        End Function

        Private MustInherit Class StoreLoadContextMenuStrip
            Inherits ContextMenuStrip
            Protected _nest As PositionManager

            Protected WithEvents tsmiSavedPosition As ToolStripMenuItem
            Protected WithEvents tsmiMemManage As ToolStripMenuItem

            Protected Sub New(ByVal nest As PositionManager)
                MyBase.New()
                _nest = nest

                'Saved Position...
                tsmiSavedPosition = New ToolStripMenuItem("Saved Position...")
                Me.Items.Add(tsmiSavedPosition)

                Me.Items.Add(New ToolStripSeparator) ' -----------------------------

                'Mem1
                Dim i = 1
                For Each mem In nest.m_memReg
                    Me.Items.Add(initValueMenuItem_safe("Pos" & i, mem, MemHandler))
                    i += 1
                Next
            End Sub

            Private Function initValueMenuItem_safe(ByVal baseTitle As String, ByVal value As Object, ByVal clickHandler As EventHandler) As ToolStripMenuItem
                Dim tempText = baseTitle
                Dim valueIsNull = value Is Nothing OrElse value.ToString = ""
                If Not valueIsNull Then tempText &= " = " & value
                Dim tsmi = New ToolStripMenuItem(tempText)
                tsmi.Enabled = Not (valueIsNull And DisableNull)
                AddHandler tsmi.Click, clickHandler
                Return tsmi
            End Function

            Protected MustOverride ReadOnly Property DisableNull() As Boolean
            Protected MustOverride ReadOnly Property MemHandler() As EventHandler

        End Class
        Private Class LoadContextMenuStrip
            Inherits StoreLoadContextMenuStrip

            Private m_f_loadPosition As Action(Of Integer)
            Private m_posMan As PositionManager

            Public Sub New(ByVal nest As PositionManager, ByVal f_loadPosition As Action(Of Integer))
                MyBase.New(nest)

                m_posMan = nest
                m_f_loadPosition = f_loadPosition
            End Sub

            Private Sub tsmiLoadSavedPosition_Click(ByVal sender As System.Object, ByVal e As System.EventArgs) Handles tsmiSavedPosition.Click
                Dim pos As New frmPosition(m_posMan.m_settings, DialogType.Load)
                If pos.ShowDialog() = DialogResult.OK Then m_f_loadPosition(pos.SelectedPosition)
            End Sub
            Private Sub tsmiLoadMem_Click(ByVal sender As System.Object, ByVal e As System.EventArgs)
                Dim tsmi As ToolStripMenuItem = sender
                Dim number As Integer = tsmi.Text.Substring(3, 1) - 1
                m_f_loadPosition.Invoke(_nest.m_memReg(number))
            End Sub
            Private Sub tsmiLoadAutorunStart_Click(ByVal sender As System.Object, ByVal e As System.EventArgs)
                m_f_loadPosition.Invoke(_nest.m_smartStepsManager.LastAutorunRun.AutorunStart)
            End Sub
            Private Sub tsmiLoadAutorunStop_Click(ByVal sender As System.Object, ByVal e As System.EventArgs)
                m_f_loadPosition.Invoke(_nest.m_smartStepsManager.LastAutorunRun.AutorunStop)
            End Sub
            Protected Overrides ReadOnly Property DisableNull() As Boolean
                Get
                    Return True
                End Get
            End Property
            Protected Overrides ReadOnly Property MemHandler() As System.EventHandler
                Get
                    Return AddressOf tsmiLoadMem_Click
                End Get
            End Property
        End Class
        Private Class StoreContextMenuStrip
            Inherits StoreLoadContextMenuStrip

            Private m_value As Integer
            Private m_posMan As PositionManager

            Public Sub New(ByVal nest As PositionManager, ByVal value As Integer)
                MyBase.New(nest)

                m_value = value
                m_posMan = nest
            End Sub

            Private Sub tsmiStoreSavedPosition_Click(ByVal sender As System.Object, ByVal e As System.EventArgs) Handles tsmiSavedPosition.Click
                Dim pos As New frmPosition(m_posMan.m_settings, DialogType.Save, m_value)
                pos.ShowDialog()
            End Sub
            Private Sub tsmiStoreMem_Click(ByVal sender As System.Object, ByVal e As System.EventArgs)
                Dim tsmi As ToolStripMenuItem = sender
                Dim number As Integer = tsmi.Text.Substring(3, 1) - 1
                _nest.m_memReg(number) = m_value
            End Sub
            Private Sub tsmiStoreAutorunStart_Click(ByVal sender As System.Object, ByVal e As System.EventArgs)
                _nest.m_smartStepsManager.LastAutorunRun.AutorunStart = m_value
            End Sub
            Private Sub tsmiStoreAutorunStop_Click(ByVal sender As System.Object, ByVal e As System.EventArgs)
                _nest.m_smartStepsManager.LastAutorunRun.AutorunStop = m_value
            End Sub
            Protected Overrides ReadOnly Property DisableNull() As Boolean
                Get
                    Return False
                End Get
            End Property
            Protected Overrides ReadOnly Property MemHandler() As System.EventHandler
                Get
                    Return AddressOf tsmiStoreMem_Click
                End Get
            End Property
        End Class

    End Class
End Namespace
